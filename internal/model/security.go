package model

import (
	"fmt"
	"time"

	"github.com/uptrace/bun"
)

// SecurityEvent is one client report. It is a signal, not proof: the
// row records whether the report carried a verified device signature,
// and automatic policy actions only count verified rows.
type SecurityEvent struct {
	bun.BaseModel `bun:"table:security_events"`

	ID         string    `bun:",pk" json:"id"`
	ProductID  string    `bun:",notnull" json:"product_id"`
	LicenseID  string    `bun:",notnull" json:"license_id"`
	EventID    string    `bun:",notnull" json:"event_id"`
	EventType  string    `bun:",notnull" json:"event_type"`
	Severity   string    `bun:",notnull,default:'medium'" json:"severity"`
	DeviceID   string    `bun:",notnull" json:"device_id"`
	Verified   bool      `bun:",notnull,default:false" json:"verified"`
	OccurredAt time.Time `bun:",notnull" json:"occurred_at"`
	ReceivedAt time.Time `bun:",notnull,default:now()" json:"received_at"`
	Source     string    `bun:",notnull,default:'sdk'" json:"source"`
	IPAddress  string    `bun:",notnull,default:''" json:"-"`
	// Details is intentionally a flat string map. The handler rejects
	// nested structures and caps every key and value, which keeps a
	// hostile client from smuggling a whole file into the database or
	// using the field to store a token.
	Details      map[string]string `bun:"type:jsonb,default:'{}'" json:"details,omitempty"`
	PolicyAction string            `bun:",notnull,default:''" json:"policy_action,omitempty"`
	PolicyRuleID string            `bun:",notnull,default:''" json:"policy_rule_id,omitempty"`
	Status       string            `bun:",notnull,default:'received'" json:"status"`
	ReviewedAt   *time.Time        `json:"reviewed_at,omitempty"`
	ReviewedBy   string            `bun:",notnull,default:''" json:"reviewed_by,omitempty"`
}

const (
	SecurityEventStatusReceived  = "received"
	SecurityEventStatusConfirmed = "confirmed"
	SecurityEventStatusDismissed = "dismissed"
)

// SecurityPolicy is a per-product reaction rule. A missing row means
// "use DefaultSecurityPolicies".
type SecurityPolicy struct {
	bun.BaseModel `bun:"table:security_event_policies"`

	ID            string    `bun:",pk" json:"id"`
	ProductID     string    `bun:",notnull" json:"product_id"`
	EventType     string    `bun:",notnull" json:"event_type"`
	Action        string    `bun:",notnull" json:"action"`
	Threshold     int       `bun:",notnull,default:1" json:"threshold"`
	WindowSeconds int       `bun:",notnull,default:86400" json:"window_seconds"`
	SuspendDays   int       `bun:",notnull,default:0" json:"suspend_days"`
	Enabled       bool      `bun:",notnull,default:true" json:"enabled"`
	CreatedAt     time.Time `bun:",nullzero,default:now()" json:"created_at"`
	UpdatedAt     time.Time `bun:",nullzero,default:now()" json:"updated_at"`
}

// Action vocabulary for a reaction policy.
//
// Only the states below are applied server-side; the client is never
// asked to enforce a lifecycle decision by itself.
const (
	// PolicyActionLog records the event and does nothing else.
	PolicyActionLog = "log"
	// PolicyActionFlag marks the licence under review. Existing
	// installs keep working.
	PolicyActionFlag = "flag"
	// PolicyActionReject refuses the current request. For a security
	// report this tells the SDK to stop the protected operation now,
	// without changing the licence for other devices/sessions.
	PolicyActionReject = "reject"
	// PolicyActionBlockActivation refuses new activations while
	// leaving installed devices usable.
	PolicyActionBlockActivation = "block_activation"
	// PolicyActionSuspend suspends the licence until an administrator
	// reinstates it, or until suspend_days elapses.
	PolicyActionSuspend = "suspend"
	// PolicyActionRevoke permanently revokes the licence. It requires
	// verified evidence and a threshold of at least 2, and is never a
	// built-in default.
	PolicyActionRevoke = "revoke"
)

// Supported event types. The list is closed so a client cannot invent
// a type that a `*` policy would then act on. `other` is the explicit
// escape hatch and is only ever logged by the default policy.
const (
	SecurityEventIntegrityViolation    = "integrity_violation"
	SecurityEventManifestInvalid       = "manifest_invalid"
	SecurityEventDeviceMismatch        = "device_mismatch"
	SecurityEventDeviceChanged         = "device_identifier_changed"
	SecurityEventTokenInvalid          = "license_token_invalid"
	SecurityEventActivationReplay      = "activation_replay"
	SecurityEventUnexpectedEnvironment = "unexpected_environment"
	SecurityEventOther                 = "other"
)

// SecurityEventTypes is the accepted set. Order matters only for docs
// and tests; membership is what the handler checks.
var SecurityEventTypes = []string{
	SecurityEventIntegrityViolation,
	SecurityEventManifestInvalid,
	SecurityEventDeviceMismatch,
	SecurityEventDeviceChanged,
	SecurityEventTokenInvalid,
	SecurityEventActivationReplay,
	SecurityEventUnexpectedEnvironment,
	SecurityEventOther,
}

// IsSecurityEventType reports whether t is one of the documented types.
func IsSecurityEventType(t string) bool {
	for _, known := range SecurityEventTypes {
		if known == t {
			return true
		}
	}
	return false
}

// DefaultSecurityPolicies is the conservative baseline used when a
// product has no matching row. It never revokes automatically and
// never suspends on a single report:
//
//   - one integrity violation flags the licence for review;
//   - three verified integrity violations in 24h suspend it;
//   - one manifest signature failure flags it (corruption and
//     tampering are indistinguishable from one client report);
//   - device changes/replays/invalid tokens are logged and flagged,
//     because a hardware swap is a legitimate support event.
//
// Administrators can override any of these per product.
func DefaultSecurityPolicies() []SecurityPolicy {
	return []SecurityPolicy{
		{EventType: SecurityEventIntegrityViolation, Action: PolicyActionSuspend, Threshold: 3, WindowSeconds: 86400},
		{EventType: SecurityEventManifestInvalid, Action: PolicyActionFlag, Threshold: 1, WindowSeconds: 86400},
		{EventType: SecurityEventDeviceMismatch, Action: PolicyActionFlag, Threshold: 1, WindowSeconds: 86400},
		{EventType: SecurityEventDeviceChanged, Action: PolicyActionFlag, Threshold: 1, WindowSeconds: 86400},
		{EventType: SecurityEventTokenInvalid, Action: PolicyActionFlag, Threshold: 1, WindowSeconds: 86400},
		{EventType: SecurityEventActivationReplay, Action: PolicyActionFlag, Threshold: 3, WindowSeconds: 86400},
		{EventType: SecurityEventUnexpectedEnvironment, Action: PolicyActionFlag, Threshold: 1, WindowSeconds: 86400},
		{EventType: SecurityEventOther, Action: PolicyActionLog, Threshold: 1, WindowSeconds: 86400},
	}
}

// DefaultSecurityPolicy returns the built-in rule for an event type.
// Unknown types fall back to log-only.
func DefaultSecurityPolicy(eventType string) SecurityPolicy {
	for _, p := range DefaultSecurityPolicies() {
		if p.EventType == eventType {
			return p
		}
	}
	return SecurityPolicy{EventType: eventType, Action: PolicyActionLog, Threshold: 1, WindowSeconds: 86400}
}

// ValidSecurityPolicyAction reports whether action is in the closed
// vocabulary.
func ValidSecurityPolicyAction(action string) bool {
	switch action {
	case PolicyActionLog, PolicyActionFlag, PolicyActionReject,
		PolicyActionBlockActivation, PolicyActionSuspend, PolicyActionRevoke:
		return true
	}
	return false
}

// ValidSecuritySeverity normalizes a client-supplied severity. Unknown
// values become "medium" rather than failing the report.
func ValidSecuritySeverity(severity string) string {
	switch severity {
	case "low", "medium", "high", "critical":
		return severity
	}
	return "medium"
}

// ValidateSecurityPolicy rejects a rule that could cause a destructive
// action from ambiguous evidence. In particular:
//
//   - revoke is never available product-wide (`*`);
//   - revoke needs at least two verified events in the window, because
//     one client report is a signal, not proof;
//   - only documented event types (or `*`) may be configured.
func ValidateSecurityPolicy(p *SecurityPolicy) error {
	if p == nil {
		return fmt.Errorf("policy is required")
	}
	if p.EventType != "*" && !IsSecurityEventType(p.EventType) {
		return fmt.Errorf("unsupported event_type %q", p.EventType)
	}
	if !ValidSecurityPolicyAction(p.Action) {
		return fmt.Errorf("unsupported action %q", p.Action)
	}
	if p.Threshold < 1 || p.Threshold > 100 {
		return fmt.Errorf("threshold must be between 1 and 100")
	}
	if p.WindowSeconds < 60 || p.WindowSeconds > 2592000 {
		return fmt.Errorf("window_seconds must be between 60 and 2592000")
	}
	if p.SuspendDays < 0 || p.SuspendDays > 3650 {
		return fmt.Errorf("suspend_days must be between 0 and 3650")
	}
	if p.Action == PolicyActionRevoke {
		if p.EventType == "*" {
			return fmt.Errorf("revoke cannot be configured as a product-wide default")
		}
		if p.Threshold < 2 {
			return fmt.Errorf("revoke requires a threshold of at least 2 verified events")
		}
	}
	return nil
}
