// Package securityevent defines the wire contract shared by Keygate
// clients and the server for tamper/security reports.
//
// A report is advisory evidence, not proof. The server records whether
// it carried a valid device signature and only lets verified reports
// drive automatic suspension or revocation. The canonical string below
// is deliberately small and stable so SDKs in other languages can
// reproduce it exactly:
//
//	keygate-security-event-v1\n
//	<event_id>\n
//	<license_id>\n
//	<device_id>\n
//	<event_type>\n
//	<occurred_at_unix_seconds>
//
// The signature is Ed25519 over the UTF-8 bytes of that string. The
// license key, the token and the details object are NOT part of the
// signed payload: the first two are secrets that should never be fed to
// a signing routine, and details are telemetry which cannot influence a
// policy decision on its own.
package securityevent

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// CanonicalVersion is the first line of every signed payload. Bump it
// only for a breaking change in the field order or separators.
const CanonicalVersion = "keygate-security-event-v1"

// Detail limits keep a hostile client from turning a telemetry field
// into bulk storage. They are enforced server-side and mirrored by the
// C# SDK.
const (
	MaxDetailsEntries   = 16
	MaxDetailKeyLength  = 64
	MaxDetailValueBytes = 512
	MaxEventIDLength    = 128
	MaxEventTypeLength  = 64
	MaxDeviceIDLength   = 256
	MaxSignatureLength  = 256
	MaxPublicKeyLength  = 128
)

// Reserved detail keys are dropped rather than rejected: a client SDK
// that accidentally includes a token must not lose the whole report,
// but the secret must never reach the database or the audit trail.
var reservedDetailKey = regexp.MustCompile(`(?i)(token|license[_-]?key|private[_-]?key|secret|password|authorization|signature|cookie)`)

var detailKeyPattern = regexp.MustCompile(`^[a-z][a-z0-9_.-]{0,63}$`)

// ErrInvalidSignature is returned when a report's signature does not
// verify against the device public key on the activation.
var ErrInvalidSignature = errors.New("security event signature is invalid")

// ErrInvalidPublicKey is returned when the stored/registered device key
// is not a 32-byte Ed25519 key.
var ErrInvalidPublicKey = errors.New("security event device public key is invalid")

// CanonicalString returns the exact bytes a client signs.
func CanonicalString(eventID, licenseID, deviceID, eventType string, occurredAtUnix int64) string {
	return CanonicalVersion + "\n" +
		eventID + "\n" +
		licenseID + "\n" +
		deviceID + "\n" +
		eventType + "\n" +
		strconv.FormatInt(occurredAtUnix, 10)
}

// CanonicalBytes is CanonicalString as UTF-8 bytes, for ed25519.Verify.
func CanonicalBytes(eventID, licenseID, deviceID, eventType string, occurredAtUnix int64) []byte {
	return []byte(CanonicalString(eventID, licenseID, deviceID, eventType, occurredAtUnix))
}

// DecodePublicKey accepts the formats activation accepts: 64 hex
// characters, base64url (padded or not), or standard base64.
func DecodePublicKey(raw string) (ed25519.PublicKey, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil, ErrInvalidPublicKey
	}
	if len(raw) == 64 {
		if b, err := hex.DecodeString(raw); err == nil && len(b) == ed25519.PublicKeySize {
			return ed25519.PublicKey(b), nil
		}
	}
	b, err := DecodeBase64Flex(raw)
	if err != nil || len(b) != ed25519.PublicKeySize {
		return nil, ErrInvalidPublicKey
	}
	return ed25519.PublicKey(b), nil
}

// DecodeSignature accepts raw-url, url or standard base64.
func DecodeSignature(raw string) ([]byte, error) {
	return DecodeBase64Flex(strings.TrimSpace(raw))
}

// Verify checks an Ed25519 signature over the canonical report string.
func Verify(publicKey ed25519.PublicKey, signature []byte, canonical []byte) error {
	if len(publicKey) != ed25519.PublicKeySize {
		return ErrInvalidPublicKey
	}
	if len(signature) != ed25519.SignatureSize {
		return ErrInvalidSignature
	}
	if !ed25519.Verify(publicKey, canonical, signature) {
		return ErrInvalidSignature
	}
	return nil
}

// DecodeBase64Flex mirrors the flexible decoder used by the integrity
// package so one set of client encodings works everywhere.
func DecodeBase64Flex(s string) ([]byte, error) {
	if b, err := base64.RawURLEncoding.DecodeString(s); err == nil {
		return b, nil
	}
	if b, err := base64.URLEncoding.DecodeString(s); err == nil {
		return b, nil
	}
	if b, err := base64.StdEncoding.DecodeString(s); err == nil {
		return b, nil
	}
	return base64.RawStdEncoding.DecodeString(s)
}

// SanitizeDetails copies a client-supplied detail map into a bounded,
// flat map. Reserved keys are dropped; unknown or oversized keys and
// values are dropped; nothing throws, so a malformed extra field never
// costs the operator the rest of the report.
func SanitizeDetails(in map[string]string) map[string]string {
	out := make(map[string]string, len(in))
	if len(in) > MaxDetailsEntries {
		// Iteration order is random; sorting first makes the truncation
		// deterministic so two identical reports store identical rows.
		keys := make([]string, 0, len(in))
		for k := range in {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		for _, k := range keys[:MaxDetailsEntries] {
			putDetail(out, k, in[k])
		}
		return out
	}
	for k, v := range in {
		putDetail(out, k, v)
	}
	return out
}

func putDetail(out map[string]string, key, value string) {
	k := strings.ToLower(strings.TrimSpace(key))
	if k == "" || len(k) > MaxDetailKeyLength || !detailKeyPattern.MatchString(k) {
		return
	}
	if reservedDetailKey.MatchString(k) {
		return
	}
	v := strings.TrimSpace(value)
	if len(v) > MaxDetailValueBytes {
		v = v[:MaxDetailValueBytes]
	}
	if strings.ContainsAny(v, "\x00\r\n") {
		// A client can send a newline through JSON; keeping it would
		// let a report forge extra lines in a log viewer later.
		v = strings.NewReplacer("\x00", "", "\r", " ", "\n", " ").Replace(v)
	}
	out[k] = v
}

// ValidateEventID enforces a conservative, log-safe event id.
func ValidateEventID(id string) error {
	if id == "" || len(id) > MaxEventIDLength {
		return fmt.Errorf("event_id must be 1-%d characters", MaxEventIDLength)
	}
	for _, r := range id {
		if !(r == '-' || r == '_' || r == '.' ||
			(r >= '0' && r <= '9') ||
			(r >= 'a' && r <= 'z') ||
			(r >= 'A' && r <= 'Z')) {
			return errors.New("event_id contains invalid characters")
		}
	}
	return nil
}
