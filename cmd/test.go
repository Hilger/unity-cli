package cmd

import (
	"bytes"
	"crypto/rand"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

type suppressWriter struct {
	w        io.Writer
	suppress string
}

func (s *suppressWriter) Write(p []byte) (int, error) {
	if bytes.Contains(p, []byte(s.suppress)) {
		return len(p), nil
	}
	return s.w.Write(p)
}

func generateRunID() string {
	b := make([]byte, 8)
	_, _ = rand.Read(b)
	return fmt.Sprintf("%x", b)
}

func testCmd(args []string, send sendFn, port int, projectPath string) (*client.CommandResponse, error) {
	flags := parseSubFlags(args)

	mode := "EditMode"
	if m, ok := flags["mode"]; ok {
		mode = m
	}

	if mode != "EditMode" && mode != "PlayMode" {
		return nil, fmt.Errorf("--mode must be EditMode or PlayMode, got: %s", mode)
	}

	runID := generateRunID()

	params := map[string]interface{}{
		"mode":  mode,
		"runId": runID,
	}
	if filter, ok := flags["filter"]; ok {
		params["filter"] = filter
	}
	if assembly, ok := flags["assembly"]; ok {
		params["assembly"] = assembly
	}

	resp, err := send("run_tests", params)
	if err != nil {
		return nil, err
	}

	if !resp.Success && strings.Contains(resp.Message, "Unknown command") {
		return nil, fmt.Errorf(
			"'run_tests' is not available.\n" +
				"Install the Unity Test Framework package:\n" +
				"  Window > Package Manager > search 'Test Framework' > Install")
	}

	if resp.Message != "running" {
		return resp, nil
	}

	fmt.Fprintf(os.Stderr, "%s tests running, waiting for results...\n", mode)

	original := log.Writer()
	log.SetOutput(&suppressWriter{w: os.Stderr, suppress: "Unsolicited response received on idle HTTP channel"})
	defer log.SetOutput(original)

	return pollTestResults(port, projectPath, runID)
}

// pollTestResults polls Logs/TestResults.json for final results and
// Logs/TestProgress.json for progress updates. The runId ensures we
// only accept results from the current test run.
func pollTestResults(port int, projectPath string, runID string) (*client.CommandResponse, error) {
	resultsPath := filepath.Join(projectPath, "Logs", "TestResults.json")
	progressPath := filepath.Join(projectPath, "Logs", "TestProgress.json")
	deadline := time.Now().Add(30 * time.Minute)
	lastProgressCount := 0

	for time.Now().Before(deadline) {
		time.Sleep(2 * time.Second)

		// Check for final results
		if data, err := os.ReadFile(resultsPath); err == nil && len(data) > 0 {
			var result struct {
				RunID   string `json:"runId"`
				Status  string `json:"status"`
				Summary struct {
					Total   int `json:"total"`
					Passed  int `json:"passed"`
					Failed  int `json:"failed"`
					Skipped int `json:"skipped"`
				} `json:"summary"`
				Tests []struct {
					Name    string `json:"name"`
					Status  string `json:"status"`
					Message string `json:"message,omitempty"`
				} `json:"tests"`
			}

			if err := json.Unmarshal(data, &result); err == nil &&
				result.RunID == runID && result.Status == "completed" {

				var failures []string
				var passes []string
				for _, t := range result.Tests {
					if t.Status == "passed" {
						passes = append(passes, t.Name)
					} else if t.Status == "failed" {
						failures = append(failures, fmt.Sprintf("%s: %s", t.Name, t.Message))
					}
				}

				message := fmt.Sprintf("All %d test(s) passed.", result.Summary.Passed)
				if result.Summary.Failed > 0 {
					message = fmt.Sprintf("%d test(s) failed.", result.Summary.Failed)
				}

				respData, _ := json.Marshal(map[string]interface{}{
					"total":    result.Summary.Total,
					"passed":   result.Summary.Passed,
					"failed":   result.Summary.Failed,
					"skipped":  result.Summary.Skipped,
					"failures": failures,
					"passes":   passes,
				})

				return &client.CommandResponse{
					Success: result.Summary.Failed == 0,
					Message: message,
					Data:    respData,
				}, nil
			}
		}

		// Show progress from the progress file
		if data, err := os.ReadFile(progressPath); err == nil && len(data) > 2 {
			var entries []json.RawMessage
			if json.Unmarshal(data, &entries) == nil && len(entries) > lastProgressCount {
				lastProgressCount = len(entries)
				fmt.Fprintf(os.Stderr, "\r  %d tests completed...", lastProgressCount)
			}
		}

		// Check if Unity is still alive
		inst, err := readStatus(port)
		if err == nil && inst.State == "stopped" {
			return nil, fmt.Errorf("unity editor has stopped (port %d)", port)
		}
	}

	return nil, fmt.Errorf("timed out waiting for test results (30m)")
}
