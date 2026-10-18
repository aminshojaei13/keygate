package main

import (
	"crypto/ed25519"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"strings"

	"github.com/tabloy/keygate/pkg/integrity"
)

func main() {
	if len(os.Args) < 2 {
		printUsage()
		os.Exit(1)
	}

	command := os.Args[1]
	args := os.Args[2:]

	switch command {
	case "create":
		runCreate(args)
	case "verify":
		runVerify(args)
	case "inspect":
		runInspect(args)
	case "help", "-h", "--help":
		printUsage()
	default:
		fmt.Fprintf(os.Stderr, "Unknown command %q. Run 'keygate-manifest help' for usage.\n", command)
		os.Exit(1)
	}
}

func printUsage() {
	fmt.Println(`Keygate Application Integrity Manifest CLI

Usage:
  keygate-manifest create   Generate and sign an integrity manifest for a release directory
  keygate-manifest verify   Verify a signed manifest and inspect installed files
  keygate-manifest inspect  Display summary of a signed manifest envelope
  keygate-manifest help     Display this help message

Run 'keygate-manifest <command> -h' for command-specific flags.`)
}

func parsePrivateKey(raw string) (ed25519.PrivateKey, error) {
	raw = strings.TrimSpace(raw)
	// Try reading file if path exists
	if _, err := os.Stat(raw); err == nil {
		content, err := os.ReadFile(raw)
		if err == nil {
			raw = strings.TrimSpace(string(content))
		}
	}

	// Try 64-hex seed
	if len(raw) == 64 {
		seed, err := hex.DecodeString(raw)
		if err == nil && len(seed) == ed25519.SeedSize {
			return ed25519.NewKeyFromSeed(seed), nil
		}
	}

	// Try base64 seed or private key
	b, err := integrity.DecodeBase64Flex(raw)
	if err == nil {
		if len(b) == ed25519.SeedSize {
			return ed25519.NewKeyFromSeed(b), nil
		}
		if len(b) == ed25519.PrivateKeySize {
			return ed25519.PrivateKey(b), nil
		}
	}

	return nil, fmt.Errorf("invalid private key: provide 32-byte seed (64 hex chars or base64) or 64-byte key")
}

func parsePublicKey(raw string) (ed25519.PublicKey, error) {
	raw = strings.TrimSpace(raw)
	if _, err := os.Stat(raw); err == nil {
		content, err := os.ReadFile(raw)
		if err == nil {
			raw = strings.TrimSpace(string(content))
		}
	}

	if len(raw) == 64 {
		pub, err := hex.DecodeString(raw)
		if err == nil && len(pub) == ed25519.PublicKeySize {
			return ed25519.PublicKey(pub), nil
		}
	}

	b, err := integrity.DecodeBase64Flex(raw)
	if err == nil && len(b) == ed25519.PublicKeySize {
		return ed25519.PublicKey(b), nil
	}

	return nil, fmt.Errorf("invalid public key: provide 32-byte key (64 hex chars or base64)")
}

func runCreate(args []string) {
	fs := flag.NewFlagSet("create", flag.ExitOnError)
	dir := fs.String("dir", "", "Root directory of release artifacts to protect (required)")
	productID := fs.String("product", "", "Product identifier, e.g. prd_01HQ8Z12AB (required)")
	version := fs.String("version", "", "Release SemVer version string, e.g. 1.2.0 (required)")
	releaseID := fs.String("release-id", "", "Optional Keygate release identifier")
	channel := fs.String("channel", "stable", "Release channel (stable, beta, dev)")
	platform := fs.String("platform", "", "Platform string (e.g. darwin, windows, linux)")
	arch := fs.String("arch", "", "Architecture (e.g. arm64, x86_64)")
	keyID := fs.String("key-id", "", "Signing key identifier (required)")
	key := fs.String("key", "", "Signing key seed (hex or base64) or path to key file (required)")
	out := fs.String("out", "integrity.manifest.json", "Output path for the signed manifest")

	_ = fs.Parse(args)

	if *dir == "" || *productID == "" || *version == "" || *keyID == "" || *key == "" {
		fmt.Fprintln(os.Stderr, "Error: --dir, --product, --version, --key-id, and --key are required.")
		fs.Usage()
		os.Exit(1)
	}

	privKey, err := parsePrivateKey(*key)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error parsing private key: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("Scanning directory: %s\n", *dir)
	entries, err := integrity.ScanReleaseDirectory(integrity.ScanOptions{
		BaseDir: *dir,
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error scanning directory: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("Collected %d protected file entries.\n", len(entries))

	manifest := &integrity.Manifest{
		ProductID:    *productID,
		ReleaseID:    *releaseID,
		Version:      *version,
		Channel:      *channel,
		Platform:     *platform,
		Arch:         *arch,
		SigningKeyID: *keyID,
		Files:        entries,
	}

	env, err := integrity.SignManifest(manifest, privKey, *keyID)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error signing manifest: %v\n", err)
		os.Exit(1)
	}

	data, err := json.MarshalIndent(env, "", "  ")
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error formatting manifest envelope: %v\n", err)
		os.Exit(1)
	}

	if err := os.WriteFile(*out, data, 0644); err != nil {
		fmt.Fprintf(os.Stderr, "Error writing output file %q: %v\n", *out, err)
		os.Exit(1)
	}

	fmt.Printf("Successfully generated signed integrity manifest -> %s\n", *out)
	fmt.Printf("  Manifest Version: %d\n", env.ManifestVersion)
	fmt.Printf("  Key ID:           %s\n", env.SigningKeyID)
	fmt.Printf("  Protected Files:  %d\n", len(entries))
}

func runVerify(args []string) {
	fs := flag.NewFlagSet("verify", flag.ExitOnError)
	manifestPath := fs.String("manifest", "integrity.manifest.json", "Path to signed integrity manifest")
	pubKeyStr := fs.String("pubkey", "", "Public verification key (hex/base64) or path to public key file (required)")
	keyID := fs.String("key-id", "", "Expected signing key ID (optional, defaults to envelope's key_id)")
	dir := fs.String("dir", "", "Directory of installed files to verify against the manifest (required)")

	_ = fs.Parse(args)

	if *pubKeyStr == "" || *dir == "" {
		fmt.Fprintln(os.Stderr, "Error: --pubkey and --dir are required.")
		fs.Usage()
		os.Exit(1)
	}

	pubKey, err := parsePublicKey(*pubKeyStr)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error parsing public key: %v\n", err)
		os.Exit(1)
	}

	rawEnv, err := os.ReadFile(*manifestPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error reading manifest %q: %v\n", *manifestPath, err)
		os.Exit(1)
	}

	var env integrity.SignedEnvelope
	if err := json.Unmarshal(rawEnv, &env); err != nil {
		fmt.Fprintf(os.Stderr, "Error decoding manifest envelope: %v\n", err)
		os.Exit(1)
	}

	targetKeyID := env.SigningKeyID
	if *keyID != "" {
		targetKeyID = *keyID
	}

	trustedKeys := map[string]ed25519.PublicKey{
		targetKeyID: pubKey,
	}

	fmt.Println("Step 1: Cryptographic signature verification...")
	manifest, err := integrity.VerifyEnvelope(&env, trustedKeys)
	if err != nil {
		fmt.Fprintf(os.Stderr, "FAILED: %v\n", err)
		os.Exit(2)
	}
	fmt.Printf("  ✓ Signature verified with key %q (%s v%s)\n",
		env.SigningKeyID, manifest.ProductID, manifest.Version)

	fmt.Println("Step 2: Installed files verification...")
	res, err := integrity.VerifyInstalledFiles(manifest, *dir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Verification engine error: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf("  Total protected files: %d\n", res.TotalFiles)
	fmt.Printf("  Passed:                %d\n", res.PassedFiles)

	if len(res.MissingFiles) > 0 {
		fmt.Printf("  Missing files (%d):\n", len(res.MissingFiles))
		for _, m := range res.MissingFiles {
			fmt.Printf("    - %s\n", m)
		}
	}

	if len(res.ModifiedFiles) > 0 {
		fmt.Printf("  Modified/tampered files (%d):\n", len(res.ModifiedFiles))
		for _, m := range res.ModifiedFiles {
			fmt.Printf("    - %s (%s)\n", m.Path, m.Reason)
		}
	}

	if len(res.Errors) > 0 {
		fmt.Printf("  Security/I/O errors (%d):\n", len(res.Errors))
		for _, e := range res.Errors {
			fmt.Printf("    - %s\n", e)
		}
	}

	if !res.Valid {
		fmt.Fprintln(os.Stderr, "\nINTEGRITY CHECK FAILED: Differences detected between disk and signed manifest.")
		os.Exit(3)
	}

	fmt.Println("\nINTEGRITY VERIFICATION PASSED: All installed files match the signed manifest exactly.")
}

func runInspect(args []string) {
	fs := flag.NewFlagSet("inspect", flag.ExitOnError)
	manifestPath := fs.String("manifest", "integrity.manifest.json", "Path to signed manifest")
	_ = fs.Parse(args)

	raw, err := os.ReadFile(*manifestPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Read file: %v\n", err)
		os.Exit(1)
	}

	var env integrity.SignedEnvelope
	if err := json.Unmarshal(raw, &env); err != nil {
		fmt.Fprintf(os.Stderr, "Parse envelope: %v\n", err)
		os.Exit(1)
	}

	payloadBytes, err := integrity.DecodeBase64Flex(env.Payload)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Decode payload: %v\n", err)
		os.Exit(1)
	}

	var m integrity.Manifest
	if err := json.Unmarshal(payloadBytes, &m); err != nil {
		fmt.Fprintf(os.Stderr, "Parse payload JSON: %v\n", err)
		os.Exit(1)
	}

	fmt.Println("=== Keygate Integrity Manifest ===")
	fmt.Printf("Format:           %s\n", env.Format)
	fmt.Printf("Manifest Version: %d\n", env.ManifestVersion)
	fmt.Printf("Algorithm:        %s\n", env.Algorithm)
	fmt.Printf("Signing Key ID:   %s\n", env.SigningKeyID)
	fmt.Printf("Signature:        %s...\n", env.Signature[:min(16, len(env.Signature))])
	fmt.Printf("Product ID:       %s\n", m.ProductID)
	fmt.Printf("Version:          %s\n", m.Version)
	fmt.Printf("Channel:          %s\n", m.Channel)
	fmt.Printf("Platform / Arch:  %s / %s\n", m.Platform, m.Arch)
	fmt.Printf("Created At:       %s\n", m.CreatedAt)
	fmt.Printf("Total Files:      %d\n", len(m.Files))
	fmt.Println("\nProtected Files:")
	for _, f := range m.Files {
		fmt.Printf("  [%10d B] %s (sha256: %s...)\n", f.Size, f.Path, f.SHA256[:12])
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
