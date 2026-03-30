package client

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Instance represents a running Unity Editor discovered from ~/.unity-cli/instances/.
type Instance struct {
	State         string `json:"state"`
	ProjectPath   string `json:"projectPath"`
	Port          int    `json:"port"`
	PID           int    `json:"pid"`
	UnityVersion  string `json:"unityVersion,omitempty"`
	Timestamp     int64  `json:"timestamp,omitempty"`
	CompileErrors bool   `json:"compileErrors,omitempty"`
}

// CommandRequest is the JSON body sent to Unity's HTTP server.
type CommandRequest struct {
	Command string      `json:"command"`
	Params  interface{} `json:"params"`
}

// CommandResponse is the JSON body returned by Unity.
// Data is raw JSON so callers can unmarshal into any shape.
type CommandResponse struct {
	Success bool            `json:"success"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
}

func instancesDir() string {
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".unity-cli", "instances")
}

// ScanInstances reads all instance files from ~/.unity-cli/instances/.
func ScanInstances() ([]Instance, error) {
	dir := instancesDir()
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}

	var instances []Instance
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			continue
		}
		var inst Instance
		if err := json.Unmarshal(data, &inst); err != nil {
			continue
		}
		instances = append(instances, inst)
	}
	return instances, nil
}

// FindByPort scans instance files and returns the one matching the given port.
func FindByPort(port int) (*Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}
	for _, inst := range instances {
		if inst.Port == port {
			return &inst, nil
		}
	}
	return nil, fmt.Errorf("no instance on port %d", port)
}

// DiscoverInstance finds a running Unity instance from ~/.unity-cli/instances/.
// If port > 0, skips discovery and connects directly.
// If project is set, matches by project path substring.
// Otherwise returns the most recently active instance.
func DiscoverInstance(project string, port int) (*Instance, error) {
	if port > 0 {
		return &Instance{ProjectPath: "override", Port: port}, nil
	}

	instances, err := ScanInstances()
	if err != nil {
		return nil, fmt.Errorf("no Unity instances found.\nIs Unity running with the Connector package?\nExpected: %s", instancesDir())
	}

	// Filter out stopped instances
	var alive []Instance
	for _, inst := range instances {
		if inst.State == "stopped" {
			continue
		}
		alive = append(alive, inst)
	}

	if len(alive) == 0 {
		return nil, fmt.Errorf("no Unity instances running")
	}

	if project != "" {
		for _, inst := range alive {
			if strings.Contains(inst.ProjectPath, project) {
				return &inst, nil
			}
		}
		return nil, fmt.Errorf("no Unity instance found for project: %s", project)
	}

	// Try to match by current working directory before falling back to timestamp
	if cwd, err := os.Getwd(); err == nil {
		cwdNorm := filepath.ToSlash(cwd)
		for _, inst := range alive {
			projNorm := filepath.ToSlash(inst.ProjectPath)
			if cwdNorm == projNorm || strings.HasPrefix(cwdNorm, projNorm+"/") {
				return &inst, nil
			}
		}
	}

	// Return the most recently updated
	best := alive[0]
	for _, inst := range alive[1:] {
		if inst.Timestamp > best.Timestamp {
			best = inst
		}
	}
	return &best, nil
}

// heartbeatState reads the heartbeat file for the given port and returns
// the current state string. Returns "" if heartbeat can't be read.
func heartbeatState(port int) string {
	inst, err := FindByPort(port)
	if err != nil {
		return ""
	}
	age := time.Since(time.UnixMilli(inst.Timestamp))
	if age > 10*time.Second {
		return "" // stale heartbeat
	}
	return inst.State
}

// isTransientState returns true if the heartbeat state indicates Unity is
// temporarily busy and will recover (domain reload, compilation, testing).
func isTransientState(state string) bool {
	switch state {
	case "reloading", "compiling", "refreshing", "testing", "entering_playmode":
		return true
	}
	return false
}

func Send(inst *Instance, command string, params interface{}, timeoutMs int) (*CommandResponse, error) {
	if params == nil {
		params = map[string]interface{}{}
	}

	body, err := json.Marshal(CommandRequest{Command: command, Params: params})
	if err != nil {
		return nil, err
	}

	url := fmt.Sprintf("http://127.0.0.1:%d/command", inst.Port)

	// Retry loop: if Unity is in a transient state (reloading, testing),
	// the HTTP server may be briefly down. Retry instead of failing immediately.
	maxRetries := 6
	retryDelay := 2 * time.Second

	for attempt := 0; ; attempt++ {
		httpClient := &http.Client{Timeout: time.Duration(timeoutMs) * time.Millisecond}
		resp, err := httpClient.Post(url, "application/json", bytes.NewReader(body))

		if err != nil {
			// Connection failed — check heartbeat to see if Unity is just busy
			state := heartbeatState(inst.Port)
			if isTransientState(state) && attempt < maxRetries {
				fmt.Fprintf(os.Stderr, "Unity is %s, retrying in %s...\n", state, retryDelay)
				time.Sleep(retryDelay)
				continue
			}
			if state != "" {
				return nil, fmt.Errorf("cannot connect to Unity (state: %s): %v", state, err)
			}
			return nil, fmt.Errorf("cannot connect to Unity at port %d: %v", inst.Port, err)
		}

		if resp.StatusCode != http.StatusOK {
			respBody, _ := io.ReadAll(resp.Body)
			resp.Body.Close()
			if len(respBody) > 0 {
				return nil, fmt.Errorf("HTTP %d from Unity: %s", resp.StatusCode, string(respBody))
			}
			return nil, fmt.Errorf("HTTP %d from Unity (command: %s)", resp.StatusCode, command)
		}

		respBody, err := io.ReadAll(resp.Body)
		resp.Body.Close()

		if err != nil || len(respBody) == 0 {
			// Empty response — check heartbeat for context
			state := heartbeatState(inst.Port)
			if isTransientState(state) && attempt < maxRetries {
				fmt.Fprintf(os.Stderr, "Unity is %s, retrying in %s...\n", state, retryDelay)
				time.Sleep(retryDelay)
				continue
			}
			if state != "" {
				return &CommandResponse{
					Success: true,
					Message: fmt.Sprintf("%s sent (Unity is %s — response unavailable)", command, state),
				}, nil
			}
			return &CommandResponse{
				Success: true,
				Message: fmt.Sprintf("%s sent (connection closed before response)", command),
			}, nil
		}

		var result CommandResponse
		if err := json.Unmarshal(respBody, &result); err != nil {
			// Unity sent a non-JSON body — treat as plain message.
			return &CommandResponse{
				Success: true,
				Message: string(respBody),
			}, nil
		}

		return &result, nil
	}
}
