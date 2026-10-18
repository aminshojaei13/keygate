package integrity

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// DefaultExcludePatterns contains standard patterns excluded from integrity manifests
var DefaultExcludePatterns = []string{
	".*",
	"*.sig",
	"*.tmp",
	"manifest*.json",
	"*.integrity.json",
	".DS_Store",
	"Thumbs.db",
}

// ScanOptions configures file scanning for manifest generation.
type ScanOptions struct {
	BaseDir         string
	Files           []string // Optional explicit file list relative to BaseDir
	ExcludePatterns []string // Glob patterns to ignore
	IncludeHidden   bool     // When true, does not ignore dotfiles
}

// matchAnyPattern checks if name matches any pattern in patterns.
func matchAnyPattern(name string, patterns []string) bool {
	for _, p := range patterns {
		matched, err := filepath.Match(p, name)
		if err == nil && matched {
			return true
		}
	}
	return false
}

// ScanReleaseDirectory scans a directory or explicit file list, computing SHA-256 digests
// and file sizes, and returns a sorted slice of FileEntry objects with validated POSIX paths.
func ScanReleaseDirectory(opts ScanOptions) ([]FileEntry, error) {
	if opts.BaseDir == "" {
		return nil, errors.New("BaseDir is required")
	}

	cleanBase, err := filepath.Abs(opts.BaseDir)
	if err != nil {
		return nil, fmt.Errorf("resolve BaseDir: %w", err)
	}

	info, err := os.Stat(cleanBase)
	if err != nil {
		return nil, fmt.Errorf("stat BaseDir: %w", err)
	}
	if !info.IsDir() {
		return nil, fmt.Errorf("BaseDir %q is not a directory", opts.BaseDir)
	}

	excludes := opts.ExcludePatterns
	if len(excludes) == 0 && !opts.IncludeHidden {
		excludes = DefaultExcludePatterns
	}

	var entries []FileEntry

	// If explicit file list was provided:
	if len(opts.Files) > 0 {
		for _, f := range opts.Files {
			posixPath := filepath.ToSlash(filepath.Clean(f))
			if err := ValidateRelativePath(posixPath); err != nil {
				return nil, fmt.Errorf("invalid explicit file path %q: %w", f, err)
			}
			absFile := filepath.Join(cleanBase, filepath.FromSlash(posixPath))
			digest, size, err := HashFile(absFile)
			if err != nil {
				return nil, fmt.Errorf("hash file %q: %w", f, err)
			}
			entries = append(entries, FileEntry{
				Path:   posixPath,
				SHA256: digest,
				Size:   size,
			})
		}
	} else {
		// Walk directory
		err = filepath.Walk(cleanBase, func(p string, fi os.FileInfo, err error) error {
			if err != nil {
				return err
			}
			rel, err := filepath.Rel(cleanBase, p)
			if err != nil {
				return err
			}
			if rel == "." {
				return nil
			}

			name := fi.Name()
			if fi.IsDir() {
				if matchAnyPattern(name, excludes) {
					return filepath.SkipDir
				}
				return nil
			}

			if matchAnyPattern(name, excludes) {
				return nil
			}

			// Convert to canonical POSIX relative path
			posixPath := filepath.ToSlash(rel)
			if err := ValidateRelativePath(posixPath); err != nil {
				return fmt.Errorf("security violation encountered during scan: %w", err)
			}

			digest, size, err := HashFile(p)
			if err != nil {
				return fmt.Errorf("hash file %q: %w", rel, err)
			}

			entries = append(entries, FileEntry{
				Path:   posixPath,
				SHA256: digest,
				Size:   size,
			})
			return nil
		})
		if err != nil {
			return nil, fmt.Errorf("walk directory: %w", err)
		}
	}

	// Deterministic sort by Path
	sort.Slice(entries, func(i, j int) bool {
		return entries[i].Path < entries[j].Path
	})

	return entries, nil
}
