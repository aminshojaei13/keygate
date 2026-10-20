package apperr

import (
	"net/mail"
	"net/url"
	"regexp"
	"strings"

	"golang.org/x/mod/semver"

	"github.com/tabloy/keygate/pkg/deviceid"
)

// semverShape: a permissive prefilter to short-circuit obvious garbage
// before golang.org/x/mod/semver does the strict 2.0 parse (which is
// what catches leading zeros, empty pre-release identifiers, etc.).
var semverShape = regexp.MustCompile(
	`^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$`,
)

// ValidateSemver returns nil iff value is a strict SemVer 2.0 version
// string. Leading zeros (e.g. `01.0.0`), empty pre-release identifiers,
// and other 2.0 violations are rejected. Empty input is rejected — call
// sites that allow "no version" must short-circuit before calling.
func ValidateSemver(value string) *AppError {
	if value == "" {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "version is required"}
	}
	if len(value) > 64 {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "version must be at most 64 characters"}
	}
	if !semverShape.MatchString(value) || !semver.IsValid("v"+value) {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "version must be SemVer 2.0 (e.g. 1.2.3 or 1.2.3-beta.1)"}
	}
	return nil
}

func ValidateEmail(email string) *AppError {
	if email == "" {
		return BadRequest("email is required")
	}
	if len(email) > 254 {
		return BadRequest("email is too long")
	}
	addr, err := mail.ParseAddress(email)
	if err != nil {
		return BadRequest("invalid email format")
	}
	if !strings.Contains(addr.Address, "@") {
		return BadRequest("invalid email format")
	}
	// ParseAddress also accepts "Name <a@b.c>" and padding; the stored
	// value must be the bare address, or no lookup would ever match it.
	if addr.Address != email {
		return BadRequest("invalid email format: use a bare address such as name@example.com")
	}
	return nil
}

func ValidateName(field, value string) *AppError {
	if value == "" {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: field + " is required"}
	}
	if len(value) > 200 {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: field + " must be at most 200 characters"}
	}
	return nil
}

func ValidateSlug(value string) *AppError {
	if value == "" {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "slug is required"}
	}
	if len(value) < 2 {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "slug must be at least 2 characters"}
	}
	if len(value) > 100 {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "slug must be at most 100 characters"}
	}
	if value[0] == '-' || value[len(value)-1] == '-' {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "slug must not start or end with a hyphen"}
	}
	for _, c := range value {
		if !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') {
			return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "slug must contain only lowercase letters, numbers, and hyphens (a-z, 0-9, -)"}
		}
	}
	return nil
}

func ValidateURL(value string) *AppError {
	if value == "" {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "url is required"}
	}
	if len(value) > 2048 {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "url is too long"}
	}
	if !strings.HasPrefix(value, "https://") && !strings.HasPrefix(value, "http://") {
		return &AppError{Status: 400, Code: "INVALID_INPUT", Message: "url must start with https:// or http://"}
	}
	return nil
}

// ValidateHTTPURL checks an absolute http(s) URL, such as a page linked
// from an email. Callers decide whether empty is allowed.
func ValidateHTTPURL(field, raw string) *AppError {
	if len(raw) > 2048 {
		return BadRequest(field + " is too long (max 2048 characters)")
	}
	u, err := url.Parse(raw)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return BadRequest(field + " must be a full http(s) URL, such as https://example.com/download")
	}
	return nil
}

// ValidateIdentifier validates an incoming device or user activation identifier
// against the Keygate device identity protocol rules.
func ValidateIdentifier(identifier string) *AppError {
	// Whitespace-only input is "missing", not malformed. Anything else
	// is validated exactly as the caller sent it: the value that reaches
	// the activation row is this same string, so trimming only here would
	// validate a different identifier than the one stored.
	if strings.TrimSpace(identifier) == "" {
		return BadRequest("identifier is required")
	}
	if len(identifier) > 256 {
		return BadRequest("identifier too long")
	}
	if err := deviceid.ValidateIdentifierFormat(identifier); err != nil {
		return BadRequest("invalid identifier format: " + err.Error())
	}
	return nil
}
