package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// editorCmd controls Unity play mode and asset database.
// port is needed for heartbeat polling (waitForPlaying, waitForReady).
func editorCmd(args []string, send sendFn, port int) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli editor <play|stop|pause|refresh>")
	}

	action := args[0]
	flags := parseSubFlags(args[1:])

	switch action {
	case "play":
		_, wait := flags["wait"]
		_, monitor := flags["monitor"]

		params := map[string]interface{}{
			"action": "play",
			// Never use wait_for_completion — it holds the HTTP connection open
			// across domain reloads, which breaks. Poll heartbeat instead.
		}

		if monitor {
			runID := generateRunID()
			params["track_session"] = true
			params["run_id"] = runID
		}

		resp, err := send("manage_editor", params)
		if err != nil {
			return nil, err
		}

		// Poll heartbeat until state is "playing" (same pattern as waitForReady)
		if wait || monitor {
			if err := waitForPlaying(port); err != nil {
				return nil, err
			}
			resp.Message = "Entered play mode (confirmed)."
		}

		if monitor {
			return monitorPlaySession(send, port)
		}

		return resp, nil

	case "stop":
		return send("manage_editor", map[string]interface{}{"action": "stop"})

	case "pause":
		return send("manage_editor", map[string]interface{}{"action": "pause"})

	case "refresh":
		_, compile := flags["compile"]
		if compile {
			resp, err := send("refresh_unity", map[string]interface{}{
				"compile": "request",
			})
			if err != nil {
				return nil, err
			}
			hasErrors := waitForReady(port)
			if hasErrors {
				return nil, fmt.Errorf("compilation finished with errors (check unity-cli console)")
			}
			resp.Message = "Refresh and compilation completed."
			return resp, nil
		}
		return send("refresh_unity", map[string]interface{}{})

	default:
		return nil, fmt.Errorf("unknown editor action: %s\nAvailable: play, stop, pause, refresh", action)
	}
}

// waitForPlaying polls the heartbeat until state is "playing".
// Mirrors waitForReady but targets the "playing" state.
func waitForPlaying(port int) error {
	fmt.Fprintf(os.Stderr, "Waiting for play mode...\n")

	deadline := time.Now().Add(60 * time.Second)
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		status, err := readStatus(port)
		if err != nil {
			continue
		}
		if status.State == "playing" {
			fmt.Fprintf(os.Stderr, "Entered play mode.\n")
			return nil
		}
		if status.State == "stopped" {
			return fmt.Errorf("Unity editor has stopped")
		}
	}

	return fmt.Errorf("timed out waiting for play mode (60s)")
}

// monitorPlaySession polls the play_session tool via HTTP until play mode ends.
// Streams errors as they appear and prints a summary on completion.
func monitorPlaySession(send sendFn, port int) (*client.CommandResponse, error) {
	fmt.Fprintf(os.Stderr, "Monitoring play session (Ctrl+C to detach)...\n")

	lastErrorCount := 0
	lastWarningCount := 0

	for {
		time.Sleep(500 * time.Millisecond)

		// Query session status via HTTP (native server survives domain reloads)
		resp, err := send("play_session", map[string]interface{}{
			"action": "status",
		})
		if err != nil {
			// Check if Unity is still alive
			inst, readErr := readStatus(port)
			if readErr != nil {
				continue // Can't read heartbeat — keep trying
			}
			if inst.State == "stopped" {
				return nil, fmt.Errorf("Unity editor has stopped (port %d)", port)
			}
			// Transient error (domain reload) — keep polling
			continue
		}

		if !resp.Success {
			continue
		}

		var status struct {
			Active   bool   `json:"active"`
			RunID    string `json:"runId"`
			Errors   int    `json:"errors"`
			Warnings int    `json:"warnings"`
			Playing  bool   `json:"playing"`
		}
		if err := json.Unmarshal(resp.Data, &status); err != nil {
			continue
		}

		// Session ended or was never active
		if !status.Active {
			return fetchPlayResults(send)
		}

		// Print new errors/warnings as they appear
		if status.Errors > lastErrorCount {
			newErrors := status.Errors - lastErrorCount
			fmt.Fprintf(os.Stderr, "  +%d error(s) (total: %d)\n", newErrors, status.Errors)
			lastErrorCount = status.Errors
		}
		if status.Warnings > lastWarningCount {
			lastWarningCount = status.Warnings
		}

		// Session is still active but play mode ended (finalizing)
		if !status.Playing {
			time.Sleep(500 * time.Millisecond)
			return fetchPlayResults(send)
		}
	}
}

// fetchPlayResults retrieves the final play session results via HTTP.
func fetchPlayResults(send sendFn) (*client.CommandResponse, error) {
	resp, err := send("play_session", map[string]interface{}{
		"action": "results",
	})
	if err != nil {
		return nil, fmt.Errorf("failed to fetch play session results: %w", err)
	}

	var results struct {
		Status          string  `json:"status"`
		DurationSeconds float64 `json:"durationSeconds"`
		Summary         struct {
			Errors      int `json:"errors"`
			Exceptions  int `json:"exceptions"`
			Warnings    int `json:"warnings"`
			TotalIssues int `json:"totalIssues"`
		} `json:"summary"`
	}

	if err := json.Unmarshal(resp.Data, &results); err == nil && results.Status == "completed" {
		fmt.Fprintf(os.Stderr, "\nPlay session ended (%.1fs)\n", results.DurationSeconds)
		if results.Summary.TotalIssues > 0 {
			fmt.Fprintf(os.Stderr, "  %d error(s), %d exception(s), %d warning(s)\n",
				results.Summary.Errors, results.Summary.Exceptions, results.Summary.Warnings)
		} else {
			fmt.Fprintf(os.Stderr, "  No errors.\n")
		}

		resp.Success = results.Summary.TotalIssues == 0
		if !resp.Success {
			resp.Message = fmt.Sprintf("%d issue(s) during play session", results.Summary.TotalIssues)
		} else {
			resp.Message = fmt.Sprintf("Play session completed cleanly (%.1fs)", results.DurationSeconds)
		}
	}

	return resp, nil
}
