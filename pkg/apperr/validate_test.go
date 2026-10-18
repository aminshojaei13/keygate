package apperr

import "testing"

func TestValidateEmail(t *testing.T) {
	tests := []struct {
		input string
		ok    bool
	}{
		{"user@example.com", true},
		{"a@b.co", true},
		{"test+tag@gmail.com", true},
		{"user@sub.domain.com", true},
		{"", false},
		{"not-email", false},
		{"@no-local.com", false},
		{"no-at-sign", false},
		{"Alice <alice@example.com>", false},          // display-name form
		{" alice@example.com ", false},                // padded
		{string(make([]byte, 255)) + "@x.com", false}, // too long
	}
	for _, tt := range tests {
		err := ValidateEmail(tt.input)
		if tt.ok && err != nil {
			t.Errorf("ValidateEmail(%q) = error %q, want nil", tt.input, err.Message)
		}
		if !tt.ok && err == nil {
			t.Errorf("ValidateEmail(%q) = nil, want error", tt.input)
		}
	}
}

func TestValidateName(t *testing.T) {
	tests := []struct {
		input string
		ok    bool
	}{
		{"Valid Name", true},
		{"X", true},
		{string(make([]byte, 200)), true},  // exactly 200
		{"", false},                        // empty
		{string(make([]byte, 201)), false}, // 201
	}
	for _, tt := range tests {
		err := ValidateName("field", tt.input)
		if tt.ok && err != nil {
			t.Errorf("ValidateName(%d chars) = error, want nil", len(tt.input))
		}
		if !tt.ok && err == nil {
			t.Errorf("ValidateName(%d chars) = nil, want error", len(tt.input))
		}
	}
}

func TestValidateSlug(t *testing.T) {
	tests := []struct {
		input string
		ok    bool
	}{
		{"my-app", true},
		{"app123", true},
		{"ab", true},
		{string(make([]byte, 100)), false}, // 100 'a's but wait - need valid chars
		{"", false},
		{"x", false}, // too short
		{"UpperCase", false},
		{"has space", false},
		{"-leading", false},
		{"trailing-", false},
		{"a@b", false},
		{"a.b", false},
		{"a_b", false},
	}
	for _, tt := range tests {
		err := ValidateSlug(tt.input)
		if tt.ok && err != nil {
			t.Errorf("ValidateSlug(%q) = error %q, want nil", tt.input, err.Message)
		}
		if !tt.ok && err == nil {
			t.Errorf("ValidateSlug(%q) = nil, want error", tt.input)
		}
	}
}

func TestValidateURL(t *testing.T) {
	tests := []struct {
		input string
		ok    bool
	}{
		{"https://example.com/webhook", true},
		{"http://localhost:9000/hook", true},
		{"https://hooks.company.io/api/v1", true},
		{"", false},
		{"example.com", false},
		{"ftp://files.com", false},
		{"not-a-url", false},
	}
	for _, tt := range tests {
		err := ValidateURL(tt.input)
		if tt.ok && err != nil {
			t.Errorf("ValidateURL(%q) = error %q, want nil", tt.input, err.Message)
		}
		if !tt.ok && err == nil {
			t.Errorf("ValidateURL(%q) = nil, want error", tt.input)
		}
	}
}

// Regression: ValidateIdentifier used to trim the identifier before
// validating it, so "device-id\n" passed and was then persisted with the
// newline intact (the API handler stores the raw string). Lookups are
// exact, so the same physical machine could hold several activation rows
// by re-sending its identifier with different padding — one slot burned
// per spelling. The canonical spelling is now the only accepted one.
func TestValidateIdentifierRejectsNonCanonicalSpelling(t *testing.T) {
	valid := []string{
		"550e8400-e29b-41d4-a716-446655440000",
		"kgid:v1:darwin:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7",
		"sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		"my-custom-host-id-1234",
	}
	for _, id := range valid {
		if err := ValidateIdentifier(id); err != nil {
			t.Errorf("ValidateIdentifier(%q) = %v, want nil", id, err)
		}
	}

	invalid := []string{
		"device-id\n",
		"device-id\r\n",
		"device-id ",
		" device-id",
		"\tdevice-id",
		"device-id\t",
		"   ",
		"",
	}
	for _, id := range invalid {
		if err := ValidateIdentifier(id); err == nil {
			t.Errorf("ValidateIdentifier(%q) = nil, want error", id)
		}
	}
}
