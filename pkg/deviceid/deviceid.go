package deviceid

import (
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"regexp"
	"sort"
	"strings"
	"unicode"
)

// Supported Schemes
const (
	SchemeKGIDv1 = "kgid-v1"
	SchemeSHA256 = "sha256-v1"
	SchemeUUID   = "uuid"
	SchemeLegacy = "legacy"
	SchemeOpaque = "opaque"
)

// Standard Platforms
const (
	PlatformDarwin  = "darwin"
	PlatformWindows = "windows"
	PlatformLinux   = "linux"
	PlatformAndroid = "android"
	PlatformIOS     = "ios"
	PlatformBrowser = "browser"
	PlatformOther   = "other"
)

// Standard Hardware Characteristic Types
const (
	ComponentBoard = "board" // Motherboard UUID / Baseboard Serial / System UUID
	ComponentCPU   = "cpu"   // Processor ID / CPU architecture signature
	ComponentDisk  = "disk"  // Root storage volume UUID / system serial
	ComponentOS    = "os"    // OS install ID / machine-id / MachineGuid / Vendor ID
	ComponentNIC   = "nic"   // Primary physical network adapter MAC address
)

// Component Weights for Drift Scoring (Sum = 100)
const (
	WeightBoard = 35
	WeightOS    = 25
	WeightDisk  = 20
	WeightCPU   = 10
	WeightNIC   = 10
)

// Thresholds for Drift Decision
const (
	ThresholdSameDevice = 60 // >= 60: Same physical device with acceptable drift
	ThresholdAmbiguous  = 30 // 30..59: Ambiguous drift (requires review or proof)
	// < 30: Distinct device
)

// Drift Decision Outcomes
const (
	DecisionAcceptSameDevice      = "accept_same_device"
	DecisionReviewAmbiguous       = "review_ambiguous"
	DecisionRejectDifferentDevice = "reject_different_device"
)

// Domain Separation Tags for Privacy-Preserving Hashing
const (
	DomainComponentPrefix = "keygate:device:v1:"
	DomainCompositePrefix = "keygate:device:v1:composite:"
)

var (
	uuidPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)
	sha256Pattern = regexp.MustCompile(`^sha256:[0-9a-fA-F]{64}$`)
	kgidPattern = regexp.MustCompile(`^kgid:v1:([a-z0-9_-]+):([0-9a-fA-F]{64})$`)
)

// ValidPlatforms defines the known platform identifiers.
var ValidPlatforms = map[string]bool{
	PlatformDarwin:  true,
	PlatformWindows: true,
	PlatformLinux:   true,
	PlatformAndroid: true,
	PlatformIOS:     true,
	PlatformBrowser: true,
	PlatformOther:   true,
}

// ValidComponents defines the recognized component types.
var ValidComponents = map[string]int{
	ComponentBoard: WeightBoard,
	ComponentOS:    WeightOS,
	ComponentDisk:  WeightDisk,
	ComponentCPU:   WeightCPU,
	ComponentNIC:   WeightNIC,
}

// DriftResult describes the outcome of comparing two device fingerprints.
type DriftResult struct {
	Score              int      `json:"score"`
	Decision           string   `json:"decision"`
	MatchedComponents  []string `json:"matched_components"`
	ChangedComponents  []string `json:"changed_components"`
	MissingComponents  []string `json:"missing_components"`
	Explanation        string   `json:"explanation"`
}

// NormalizeValue prepares a raw characteristic string for consistent hashing.
// Trims whitespace, strips control characters, and converts to lowercase.
func NormalizeValue(raw string) string {
	var sb strings.Builder
	for _, r := range strings.TrimSpace(raw) {
		if !unicode.IsControl(r) {
			sb.WriteRune(unicode.ToLower(r))
		}
	}
	return sb.String()
}

// HashComponent hashes a normalized hardware characteristic with domain separation.
// Raw hardware serials and MACs are never transmitted; only this digest is used.
func HashComponent(compType string, rawValue string) string {
	norm := NormalizeValue(rawValue)
	if norm == "" {
		return ""
	}
	h := sha256.New()
	h.Write([]byte(DomainComponentPrefix + compType + ":" + norm))
	return hex.EncodeToString(h.Sum(nil))
}

// ComputeCompositeDigest derives a canonical 64-hex SHA-256 fingerprint from
// a set of pre-hashed components for a given platform.
func ComputeCompositeDigest(platform string, componentHashes map[string]string) (string, error) {
	normPlatform := strings.ToLower(strings.TrimSpace(platform))
	if normPlatform == "" {
		normPlatform = PlatformOther
	}

	keys := make([]string, 0, len(componentHashes))
	for k, v := range componentHashes {
		if strings.TrimSpace(v) != "" {
			keys = append(keys, strings.ToLower(strings.TrimSpace(k)))
		}
	}
	if len(keys) == 0 {
		return "", errors.New("cannot compute composite digest: no component hashes provided")
	}
	sort.Strings(keys)

	var b strings.Builder
	b.WriteString(DomainCompositePrefix)
	b.WriteString(normPlatform)
	b.WriteString(":")
	for i, k := range keys {
		if i > 0 {
			b.WriteString(";")
		}
		b.WriteString(k)
		b.WriteString("=")
		b.WriteString(strings.ToLower(strings.TrimSpace(componentHashes[k])))
	}

	h := sha256.Sum256([]byte(b.String()))
	return hex.EncodeToString(h[:]), nil
}

// FormatKGID constructs a version 1 Keygate Device Identifier.
func FormatKGID(platform string, compositeDigest string) string {
	normPlatform := strings.ToLower(strings.TrimSpace(platform))
	if normPlatform == "" {
		normPlatform = PlatformOther
	}
	return fmt.Sprintf("kgid:v1:%s:%s", normPlatform, strings.ToLower(strings.TrimSpace(compositeDigest)))
}

// ParseIdentifier inspects an identifier string and returns its scheme, platform (if kgid),
// and canonical digest or value.
func ParseIdentifier(identifier string) (scheme string, platform string, digest string, err error) {
	identifier = strings.TrimSpace(identifier)
	if identifier == "" {
		return "", "", "", errors.New("identifier cannot be empty")
	}

	if m := kgidPattern.FindStringSubmatch(identifier); len(m) == 3 {
		return SchemeKGIDv1, m[1], strings.ToLower(m[2]), nil
	}
	if strings.HasPrefix(identifier, "kgid:") {
		return "", "", "", fmt.Errorf("malformed kgid identifier: %s", identifier)
	}

	if sha256Pattern.MatchString(identifier) {
		parts := strings.SplitN(identifier, ":", 2)
		return SchemeSHA256, "", strings.ToLower(parts[1]), nil
	}
	if strings.HasPrefix(identifier, "sha256:") {
		return "", "", "", fmt.Errorf("malformed sha256 identifier: %s", identifier)
	}

	if strings.HasPrefix(identifier, "uuid:") {
		rawUUID := strings.TrimPrefix(identifier, "uuid:")
		if !uuidPattern.MatchString(rawUUID) {
			return "", "", "", fmt.Errorf("malformed uuid identifier: %s", identifier)
		}
		return SchemeUUID, "", strings.ToLower(rawUUID), nil
	}

	if uuidPattern.MatchString(identifier) {
		return SchemeUUID, "", strings.ToLower(identifier), nil
	}

	// Opaque / legacy
	return SchemeOpaque, "", identifier, nil
}

// ValidateIdentifierFormat validates the syntactic shape of any identifier.
// Rejects control characters, whitespace, invalid prefixes, or length violations.
func ValidateIdentifierFormat(identifier string) error {
	if identifier == "" {
		return errors.New("identifier is required")
	}
	// Reject surrounding whitespace instead of trimming it. The caller
	// persists the same string it validated, and database lookups are
	// exact, so silently trimming here would validate one identifier
	// ("abc") while storing another ("abc\n") — a second row that takes
	// a second activation slot for the same physical machine. Only the
	// canonical spelling is accepted; clients send the identifier they
	// derived, not a padded copy.
	if identifier != strings.TrimSpace(identifier) {
		return errors.New("identifier must not begin or end with whitespace")
	}
	if len(identifier) > 256 {
		return errors.New("identifier exceeds 256 characters")
	}

	for _, r := range identifier {
		if unicode.IsControl(r) || unicode.IsSpace(r) {
			return errors.New("identifier contains illegal whitespace or control characters")
		}
	}

	if strings.HasPrefix(identifier, "kgid:") {
		m := kgidPattern.FindStringSubmatch(identifier)
		if len(m) != 3 {
			return errors.New("invalid kgid format; expected kgid:v1:<platform>:<64-hex>")
		}
		if !ValidPlatforms[m[1]] {
			return fmt.Errorf("unrecognized platform %q in kgid", m[1])
		}
		if len(m[2]) != 64 {
			return errors.New("invalid kgid digest length; expected 64 hex characters")
		}
		return nil
	}

	if strings.HasPrefix(identifier, "sha256:") {
		if !sha256Pattern.MatchString(identifier) {
			return errors.New("invalid sha256 format; expected sha256:<64-hex>")
		}
		return nil
	}

	if strings.HasPrefix(identifier, "uuid:") {
		rawUUID := strings.TrimPrefix(identifier, "uuid:")
		if !uuidPattern.MatchString(rawUUID) {
			return errors.New("invalid uuid format after prefix")
		}
		return nil
	}

	return nil
}

// EvaluateDrift assesses the similarity between a previously registered baseline component map
// and an incoming device component map.
func EvaluateDrift(baseline, incoming map[string]string) DriftResult {
	var matched, changed, missing []string
	matchedWeight := 0
	changedWeight := 0
	missingWeight := 0

	// Normalize incoming keys
	normBase := make(map[string]string, len(baseline))
	for k, v := range baseline {
		if strings.TrimSpace(v) != "" {
			normBase[strings.ToLower(strings.TrimSpace(k))] = strings.ToLower(strings.TrimSpace(v))
		}
	}

	normInc := make(map[string]string, len(incoming))
	for k, v := range incoming {
		if strings.TrimSpace(v) != "" {
			normInc[strings.ToLower(strings.TrimSpace(k))] = strings.ToLower(strings.TrimSpace(v))
		}
	}

	for comp, weight := range ValidComponents {
		bVal, hasBase := normBase[comp]
		iVal, hasInc := normInc[comp]

		switch {
		case hasBase && hasInc:
			if bVal == iVal {
				matchedWeight += weight
				matched = append(matched, comp)
			} else {
				changedWeight += weight
				changed = append(changed, comp)
			}
		case hasBase != hasInc:
			missingWeight += weight
			missing = append(missing, comp)
		default:
			// Neither baseline nor incoming has this component (e.g. mobile sandbox)
		}
	}

	sort.Strings(matched)
	sort.Strings(changed)
	sort.Strings(missing)

	comparableWeight := matchedWeight + changedWeight
	if comparableWeight == 0 && missingWeight == 0 {
		return DriftResult{
			Score:             0,
			Decision:          DecisionRejectDifferentDevice,
			MatchedComponents: matched,
			ChangedComponents: changed,
			MissingComponents: missing,
			Explanation:       "no comparable hardware characteristics provided",
		}
	}

	// Score calculation normalized against effective available hardware weight
	// Missing components contribute half their weight to the denominator to penalize
	// dropped attributes without instantly disabling restricted environments.
	totalWeight := float64(comparableWeight) + (float64(missingWeight) * 0.5)
	score := int((float64(matchedWeight) / totalWeight) * 100.0)
	if score > 100 {
		score = 100
	}
	if score < 0 {
		score = 0
	}

	var decision string
	var explanation string

	switch {
	case score >= ThresholdSameDevice:
		decision = DecisionAcceptSameDevice
		if len(changed) == 0 && len(missing) == 0 {
			explanation = "identical hardware fingerprint"
		} else {
			explanation = fmt.Sprintf("acceptable hardware drift (changed: %s)", strings.Join(changed, ", "))
		}
	case score >= ThresholdAmbiguous:
		decision = DecisionReviewAmbiguous
		explanation = fmt.Sprintf("ambiguous drift requiring verification (changed: %s, missing: %s)",
			strings.Join(changed, ", "), strings.Join(missing, ", "))
	default:
		decision = DecisionRejectDifferentDevice
		explanation = fmt.Sprintf("distinct hardware signature (score %d < %d)", score, ThresholdAmbiguous)
	}

	return DriftResult{
		Score:             score,
		Decision:          decision,
		MatchedComponents: matched,
		ChangedComponents: changed,
		MissingComponents: missing,
		Explanation:       explanation,
	}
}

// VerifyProofOfPossession validates an optional Ed25519 signature generated by a
// platform-supported hardware key (Secure Enclave / TPM) over a verification challenge.
func VerifyProofOfPossession(pubKeyStr string, challenge []byte, sigStr string) (bool, error) {
	pubKeyStr = strings.TrimSpace(pubKeyStr)
	sigStr = strings.TrimSpace(sigStr)

	if pubKeyStr == "" || sigStr == "" {
		return false, errors.New("public key and signature are required")
	}

	// Try hex decode first, then standard base64/url-base64
	var pubKeyBytes []byte
	var err error
	if len(pubKeyStr) == 64 {
		pubKeyBytes, err = hex.DecodeString(pubKeyStr)
	}
	if err != nil || len(pubKeyBytes) != ed25519.PublicKeySize {
		pubKeyBytes, err = base64.RawURLEncoding.DecodeString(pubKeyStr)
		if err != nil {
			pubKeyBytes, err = base64.StdEncoding.DecodeString(pubKeyStr)
		}
	}
	if err != nil || len(pubKeyBytes) != ed25519.PublicKeySize {
		return false, fmt.Errorf("invalid ed25519 public key: must be 32 bytes (%d bytes received)", len(pubKeyBytes))
	}

	// Decode signature
	var sigBytes []byte
	sigBytes, err = base64.RawURLEncoding.DecodeString(sigStr)
	if err != nil {
		sigBytes, err = base64.StdEncoding.DecodeString(sigStr)
	}
	if err != nil || len(sigBytes) != ed25519.SignatureSize {
		return false, fmt.Errorf("invalid ed25519 signature: must be 64 bytes (%d bytes received)", len(sigBytes))
	}

	return ed25519.Verify(pubKeyBytes, challenge, sigBytes), nil
}
