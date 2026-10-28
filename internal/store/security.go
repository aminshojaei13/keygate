package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"time"

	"github.com/uptrace/bun"

	"github.com/tabloy/keygate/internal/model"
)

// CreateSecurityEvent inserts a report. The unique (product_id,
// event_id) index makes this idempotent: a retry or a replayed request
// inserts nothing and returns duplicate=true, so policy actions run at
// most once per client event.
func (s *Store) CreateSecurityEvent(ctx context.Context, ev *model.SecurityEvent) (duplicate bool, err error) {
	if ev.ID == "" {
		ev.ID = newID()
	}
	if ev.ReceivedAt.IsZero() {
		ev.ReceivedAt = time.Now()
	}
	if ev.Details == nil {
		ev.Details = map[string]string{}
	}
	res, err := s.DB.NewInsert().Model(ev).
		On("CONFLICT (product_id, event_id) DO NOTHING").
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, err := res.RowsAffected()
	if err != nil {
		return false, err
	}
	return n == 0, nil
}

// CountSecurityEventsSince counts accepted reports of a type for a
// licence. verifiedOnly is what policy evaluation uses: an unsigned
// report can never accumulate toward suspension or revocation.
func (s *Store) CountSecurityEventsSince(ctx context.Context, licenseID, eventType string, since time.Time, verifiedOnly bool) (int, error) {
	q := s.DB.NewSelect().Model((*model.SecurityEvent)(nil)).
		Where("license_id = ?", licenseID).
		Where("event_type = ?", eventType).
		Where("received_at >= ?", since)
	if verifiedOnly {
		q = q.Where("verified = true")
	}
	return q.Count(ctx)
}

// CountAllSecurityEventsSince backs the per-licence flood guard. It
// counts every accepted report, verified or not, because the point is
// DB write volume rather than evidence quality.
func (s *Store) CountAllSecurityEventsSince(ctx context.Context, licenseID string, since time.Time) (int, error) {
	return s.DB.NewSelect().Model((*model.SecurityEvent)(nil)).
		Where("license_id = ?", licenseID).
		Where("received_at >= ?", since).
		Count(ctx)
}

// SecurityEventFilter narrows an admin listing.
type SecurityEventFilter struct {
	ProductID string
	LicenseID string
	Status    string
	EventType string
	Verified  *bool
	Offset    int
	Limit     int
}

// ListSecurityEvents returns newest first plus the total matching the
// filter.
func (s *Store) ListSecurityEvents(ctx context.Context, f SecurityEventFilter) ([]*model.SecurityEvent, int, error) {
	q := s.DB.NewSelect().Model((*model.SecurityEvent)(nil)).
		OrderExpr("received_at DESC, id DESC")
	if f.ProductID != "" {
		q = q.Where("product_id = ?", f.ProductID)
	}
	if f.LicenseID != "" {
		q = q.Where("license_id = ?", f.LicenseID)
	}
	if f.Status != "" {
		q = q.Where("status = ?", f.Status)
	}
	if f.EventType != "" {
		q = q.Where("event_type = ?", f.EventType)
	}
	if f.Verified != nil {
		q = q.Where("verified = ?", *f.Verified)
	}
	total, err := q.Count(ctx)
	if err != nil {
		return nil, 0, err
	}
	var out []*model.SecurityEvent
	err = q.Offset(f.Offset).Limit(f.Limit).Scan(ctx, &out)
	return out, total, err
}

// FindSecurityEvent loads one report by row id.
func (s *Store) FindSecurityEvent(ctx context.Context, id string) (*model.SecurityEvent, error) {
	ev := new(model.SecurityEvent)
	err := s.DB.NewSelect().Model(ev).Where("id = ?", id).Scan(ctx)
	return ev, err
}

// ReviewSecurityEvent marks a report confirmed or dismissed. It never
// changes the licence; callers applying an action do that separately so
// the audit trail can name both the event and the state change.
func (s *Store) ReviewSecurityEvent(ctx context.Context, id, status, reviewer string) error {
	res, err := s.DB.NewUpdate().Model((*model.SecurityEvent)(nil)).
		Set("status = ?, reviewed_at = ?, reviewed_by = ?", status, time.Now(), reviewer).
		Where("id = ?", id).
		Exec(ctx)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

// ListSecurityPolicies returns the product's explicit rules. An empty
// result means the built-in conservative defaults apply.
func (s *Store) ListSecurityPolicies(ctx context.Context, productID string) ([]*model.SecurityPolicy, error) {
	var out []*model.SecurityPolicy
	err := s.DB.NewSelect().Model(&out).
		Where("product_id = ?", productID).
		OrderExpr("event_type ASC").
		Scan(ctx)
	return out, err
}

// ReplaceSecurityPolicies swaps the whole rule set for a product in one
// transaction. Partial updates would leave a window where a removed
// "revoke" rule is gone but the new rules are not yet in place.
func (s *Store) ReplaceSecurityPolicies(ctx context.Context, productID string, rules []*model.SecurityPolicy) error {
	return s.DB.RunInTx(ctx, nil, func(ctx context.Context, tx bun.Tx) error {
		if _, err := tx.NewDelete().Model((*model.SecurityPolicy)(nil)).
			Where("product_id = ?", productID).Exec(ctx); err != nil {
			return err
		}
		now := time.Now()
		for _, r := range rules {
			r.ID = newID()
			r.ProductID = productID
			r.CreatedAt = now
			r.UpdatedAt = now
			if _, err := tx.NewInsert().Model(r).Exec(ctx); err != nil {
				return err
			}
		}
		return nil
	})
}

// MarkLicenseUnderReview sets the reversible review flag. Repeating the
// call keeps the original timestamp: the first suspicious report is the
// one support needs to see.
func (s *Store) MarkLicenseUnderReview(ctx context.Context, licenseID, reason string) (bool, error) {
	res, err := s.DB.NewUpdate().Model((*model.License)(nil)).
		Set("under_review = true").
		Set("under_review_at = COALESCE(under_review_at, ?)", time.Now()).
		Set("under_review_reason = ?", reason).
		Set("updated_at = ?", time.Now()).
		Where("id = ?", licenseID).
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	return n > 0, nil
}

// ClearLicenseReview lifts the review flag without touching status.
func (s *Store) ClearLicenseReview(ctx context.Context, licenseID string) (bool, error) {
	res, err := s.DB.NewUpdate().Model((*model.License)(nil)).
		Set("under_review = false").
		Set("under_review_at = NULL").
		Set("under_review_reason = ''").
		Set("updated_at = ?", time.Now()).
		Where("id = ?", licenseID).
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	return n > 0, nil
}

// SetLicenseActivationBlocked switches new-activation refusal on or off.
func (s *Store) SetLicenseActivationBlocked(ctx context.Context, licenseID string, blocked bool) (bool, error) {
	res, err := s.DB.NewUpdate().Model((*model.License)(nil)).
		Set("activation_blocked = ?", blocked).
		Set("updated_at = ?", time.Now()).
		Where("id = ?", licenseID).
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	return n > 0, nil
}

// SuspendLicenseForSecurity applies an automatic, optionally timed
// suspension. It deliberately leaves activations in place: a suspension
// is reversible, and support may need the device list to decide whether
// to lift it.
//
// suspendDays == 0 means "until an administrator reinstates it".
func (s *Store) SuspendLicenseForSecurity(ctx context.Context, licenseID string, suspendDays int) (bool, error) {
	now := time.Now()
	var until *time.Time
	if suspendDays > 0 {
		t := now.Add(time.Duration(suspendDays) * 24 * time.Hour)
		until = &t
	}
	res, err := s.DB.NewUpdate().Model((*model.License)(nil)).
		Set("status = ?", model.StatusSuspended).
		Set("suspended_at = ?", now).
		Set("suspended_by = ?", model.SuspendedBySecurityPolicy).
		Set("suspended_until = ?", until).
		Set("updated_at = ?", now).
		Where("id = ?", licenseID).
		Where("status IN (?)", bun.In([]string{
			model.StatusActive, model.StatusTrialing, model.StatusPastDue,
		})).
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	if n > 0 {
		_, _ = s.DB.NewRaw(`UPDATE subscriptions SET status = ?, updated_at = now() WHERE license_id = ?`,
			model.StatusSuspended, licenseID).Exec(ctx)
	}
	return n > 0, nil
}

// RevokeLicenseForSecurity is the last-resort automatic action. Callers
// must require verified evidence and a repeated-event threshold; this
// method itself only refuses to touch an already revoked licence.
func (s *Store) RevokeLicenseForSecurity(ctx context.Context, licenseID string) (bool, error) {
	res, err := s.DB.NewUpdate().Model((*model.License)(nil)).
		Set("status = ?", model.StatusRevoked).
		Set("updated_at = ?", time.Now()).
		Where("id = ?", licenseID).
		Where("status != ?", model.StatusRevoked).
		Exec(ctx)
	if err != nil {
		return false, err
	}
	n, _ := res.RowsAffected()
	if n > 0 {
		_, _ = s.DB.NewRaw(`UPDATE subscriptions SET status = ?, updated_at = now() WHERE license_id = ?`,
			model.StatusRevoked, licenseID).Exec(ctx)
	}
	return n > 0, nil
}

// UpdateActivationDeviceKey records the device's report-signing public
// key on first activation (or on a legacy activation upgrading to
// signed reports).
func (s *Store) UpdateActivationDeviceKey(ctx context.Context, activationID, publicKey, alg string) error {
	res, err := s.DB.NewUpdate().Model((*model.Activation)(nil)).
		Set("device_public_key = ?, device_key_alg = ?", publicKey, alg).
		Where("id = ?", activationID).
		Exec(ctx)
	if err != nil {
		return err
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return sql.ErrNoRows
	}
	return nil
}

// FindActivationByDeviceKey looks up an activation by the public key it
// registered, for diagnostics. Not used in the hot path.
func (s *Store) FindActivationByDeviceKey(ctx context.Context, licenseID, publicKey string) (*model.Activation, error) {
	act := new(model.Activation)
	err := s.DB.NewSelect().Model(act).
		Where("license_id = ?", licenseID).
		Where("device_public_key = ?", publicKey).
		Scan(ctx)
	return act, err
}

// ErrSecurityPolicyNotFound is returned when an admin targets a rule
// that does not belong to the requested product.
var ErrSecurityPolicyNotFound = errors.New("security policy not found")

func securityPolicyValidationError(format string, args ...any) error {
	return fmt.Errorf(format, args...)
}
