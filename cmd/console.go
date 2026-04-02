package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// consoleCmd reads console logs — prefers the JSON file for reliability,
// falls back to the reflection-based console tool if the file doesn't exist.
func consoleCmd(args []string, send sendFn, projectPath string) (*client.CommandResponse, error) {
	flags := parseSubFlags(args)

	// --clear always goes through HTTP (needs C# side to clear)
	if _, ok := flags["clear"]; ok {
		return send("console_log", map[string]interface{}{"action": "clear"})
	}

	typeFilter := "error,warning,log"
	if t, ok := flags["type"]; ok {
		typeFilter = t
	}

	lines := 30
	if l, ok := flags["lines"]; ok {
		fmt.Sscanf(l, "%d", &lines)
	}
	if l, ok := flags["count"]; ok {
		fmt.Sscanf(l, "%d", &lines)
	}

	stacktrace := "none"
	if s, ok := flags["stacktrace"]; ok {
		stacktrace = s
	}

	// Try file-based reading first (faster, no HTTP, no reflection)
	logPath := filepath.Join(projectPath, "Logs", "ConsoleLog.json")
	if entries, err := readConsoleFile(logPath, typeFilter, lines, stacktrace); err == nil {
		return entries, nil
	}

	// Fall back to reflection-based console tool
	return send("console", map[string]interface{}{
		"type":       typeFilter,
		"lines":      lines,
		"stacktrace": stacktrace,
	})
}

func readConsoleFile(path string, typeFilter string, maxLines int, stacktrace string) (*client.CommandResponse, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if len(data) < 3 { // "[]" minimum
		return nil, fmt.Errorf("empty log file")
	}

	var entries []struct {
		Seq     int    `json:"seq"`
		Type    string `json:"type"`
		Ts      int64  `json:"ts"`
		Message string `json:"message"`
		Stack   string `json:"stack"`
	}
	if err := json.Unmarshal(data, &entries); err != nil {
		return nil, err
	}

	// Build type filter set
	types := map[string]bool{}
	for _, t := range strings.Split(typeFilter, ",") {
		t = strings.TrimSpace(strings.ToLower(t))
		types[t] = true
		if t == "error" {
			types["exception"] = true
			types["assert"] = true
		}
	}

	// Filter and format — take last N matching entries
	var formatted []string
	for i := len(entries) - 1; i >= 0 && len(formatted) < maxLines; i-- {
		e := entries[i]
		typeLower := strings.ToLower(e.Type)
		if !types[typeLower] {
			continue
		}
		formatted = append(formatted, formatLogEntry(e.Message, e.Stack, stacktrace))
	}

	// Reverse to chronological order
	for i, j := 0, len(formatted)-1; i < j; i, j = i+1, j-1 {
		formatted[i], formatted[j] = formatted[j], formatted[i]
	}

	respData, _ := json.Marshal(formatted)
	return &client.CommandResponse{
		Success: true,
		Message: fmt.Sprintf("Retrieved %d entries.", len(formatted)),
		Data:    respData,
	}, nil
}

func formatLogEntry(message, stack, mode string) string {
	switch mode {
	case "full":
		if stack != "" {
			return message + "\n" + stack
		}
		return message
	case "short":
		var lines []string
		allLines := strings.Split(message+"\n"+stack, "\n")
		for _, line := range allLines {
			line = strings.TrimSpace(line)
			if line == "" {
				continue
			}
			// Skip Unity/system internal frames
			if strings.Contains(line, "UnityEngine.Debug:") ||
				strings.Contains(line, "UnityEditor.EditorGUIUtility:") ||
				strings.Contains(line, "(at Library/") ||
				strings.Contains(line, "(at ./Library/") {
				continue
			}
			lines = append(lines, line)
		}
		return strings.Join(lines, "\n")
	default: // "none"
		if idx := strings.IndexAny(message, "\n\r"); idx >= 0 {
			return message[:idx]
		}
		return message
	}
}
