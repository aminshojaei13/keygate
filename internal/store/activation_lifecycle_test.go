package store_test

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"github.com/tabloy/keygate/internal/model"
	"github.com/tabloy/keygate/internal/store"
)

func TestActivationLifecycle_MetadataAndScheme(t *testing.T) {
	s := setupTestDB(t)
	defer s.Close()
	ctx := context.Background()

	lic := activationLicense(t, s, ctx, 3)

	cases := []struct {
		name       string
		identifier string
		wantScheme string
	}{
		{
			name:       "uuid identifier",
			identifier: "550e8400-e29b-41d4-a716-446655440000",
			wantScheme: model.ActivationSchemeUUID,
		},
		{
			name:       "sha256 identifier",
			identifier: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
			wantScheme: model.ActivationSchemeSHA256,
		},
		{
			name:       "opaque identifier",
			identifier: "my-custom-host-id-1234",
			wantScheme: model.ActivationSchemeOpaque,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			act := &model.Activation{
				LicenseID:      lic.ID,
				Identifier:     tc.identifier,
				IdentifierType: "device",
				Scheme:         model.DetectIdentifierScheme(tc.identifier),
				Status:         model.ActivationActive,
				Label:          "Test Device",
				IPAddress:      "192.0.2.1",
			}
			if err := s.ActivateWithinLimit(ctx, act, 3); err != nil {
				t.Fatalf("activate: %v", err)
			}

			found, err := s.FindActivation(ctx, lic.ID, tc.identifier)
			if err != nil {
				t.Fatalf("find activation: %v", err)
			}
			if found.ID == "" {
				t.Error("expected internal activation ID to be populated")
			}
			if found.LicenseID != lic.ID {
				t.Errorf("LicenseID = %q, want %q", found.LicenseID, lic.ID)
			}
			if found.Scheme != tc.wantScheme {
				t.Errorf("Scheme = %q, want %q", found.Scheme, tc.wantScheme)
			}
			if found.Status != model.ActivationActive {
				t.Errorf("Status = %q, want %q", found.Status, model.ActivationActive)
			}
			if found.CreatedAt.IsZero() {
				t.Error("CreatedAt must not be zero")
			}
			if found.LastVerified.IsZero() {
				t.Error("LastVerified must not be zero")
			}
			if found.DeactivatedAt != nil {
				t.Errorf("DeactivatedAt = %v, want nil", found.DeactivatedAt)
			}
			if found.DeactivatedReason != "" {
				t.Errorf("DeactivatedReason = %q, want empty", found.DeactivatedReason)
			}
		})
	}
}

func TestActivationLifecycle_DeactivationAndReactivation(t *testing.T) {
	s := setupTestDB(t)
	defer s.Close()
	ctx := context.Background()

	lic := activationLicense(t, s, ctx, 2)
	ident := "device-deactivate-reactivate"

	act := &model.Activation{
		LicenseID:      lic.ID,
		Identifier:     ident,
		IdentifierType: "device",
		Scheme:         model.DetectIdentifierScheme(ident),
		Status:         model.ActivationActive,
		Label:          "Deactivate Test",
	}
	if err := s.ActivateWithinLimit(ctx, act, 2); err != nil {
		t.Fatalf("initial activate: %v", err)
	}

	// 1. Soft deactivation
	ok, err := s.SoftDeactivateActivation(ctx, act.ID, "client")
	if err != nil {
		t.Fatalf("soft deactivate: %v", err)
	}
	if !ok {
		t.Fatal("expected soft deactivate to return true")
	}

	// Double-deactivate is idempotent and returns false
	ok, err = s.SoftDeactivateActivation(ctx, act.ID, "client")
	if err != nil {
		t.Fatalf("second soft deactivate: %v", err)
	}
	if ok {
		t.Fatal("expected second soft deactivate to return false")
	}

	// Count should now be 0
	count, err := s.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 0 {
		t.Errorf("count = %d, want 0 after deactivation", count)
	}

	// Verify FindActivation doesn't see deactivated device
	if _, err := s.FindActivation(ctx, lic.ID, ident); err == nil {
		t.Error("expected FindActivation to fail for deactivated device")
	}

	// Record remains inspectable by ID
	deactRow, err := s.FindActivationByID(ctx, act.ID)
	if err != nil {
		t.Fatalf("find by id: %v", err)
	}
	if deactRow.Status != model.ActivationDeactivated {
		t.Errorf("Status = %q, want %q", deactRow.Status, model.ActivationDeactivated)
	}
	if deactRow.DeactivatedAt == nil {
		t.Error("expected DeactivatedAt to be set")
	}
	if deactRow.DeactivatedReason != "client" {
		t.Errorf("DeactivatedReason = %q, want client", deactRow.DeactivatedReason)
	}

	// 2. Reactivate via ReactivateActivation
	if err := s.ReactivateActivation(ctx, act.ID); err != nil {
		t.Fatalf("reactivate: %v", err)
	}

	// Count should now be 1 again
	count, err = s.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count after reactivate: %v", err)
	}
	if count != 1 {
		t.Errorf("count = %d, want 1 after reactivate", count)
	}

	reactRow, err := s.FindActivationByID(ctx, act.ID)
	if err != nil {
		t.Fatalf("find by id: %v", err)
	}
	if reactRow.Status != model.ActivationActive {
		t.Errorf("Status = %q, want %q", reactRow.Status, model.ActivationActive)
	}
	if reactRow.DeactivatedAt != nil {
		t.Errorf("DeactivatedAt = %v, want nil", reactRow.DeactivatedAt)
	}
	if reactRow.DeactivatedReason != "" {
		t.Errorf("DeactivatedReason = %q, want empty", reactRow.DeactivatedReason)
	}
}

func TestActivationLifecycle_ReactivateAtCapRefused(t *testing.T) {
	s := setupTestDB(t)
	defer s.Close()
	ctx := context.Background()

	// License with max=1
	lic := activationLicense(t, s, ctx, 1)

	// Machine A activates
	actA := &model.Activation{
		LicenseID:      lic.ID,
		Identifier:     "machine-A",
		IdentifierType: "device",
		Scheme:         model.ActivationSchemeOpaque,
		Status:         model.ActivationActive,
	}
	if err := s.ActivateWithinLimit(ctx, actA, 1); err != nil {
		t.Fatalf("activate A: %v", err)
	}

	// Machine A deactivates
	if _, err := s.SoftDeactivateActivation(ctx, actA.ID, "portal"); err != nil {
		t.Fatalf("deactivate A: %v", err)
	}

	// Machine B activates into the freed slot
	actB := &model.Activation{
		LicenseID:      lic.ID,
		Identifier:     "machine-B",
		IdentifierType: "device",
		Scheme:         model.ActivationSchemeOpaque,
		Status:         model.ActivationActive,
	}
	if err := s.ActivateWithinLimit(ctx, actB, 1); err != nil {
		t.Fatalf("activate B: %v", err)
	}

	// Attempting to reactivate Machine A must fail with ErrActivationLimit
	err := s.ReactivateActivation(ctx, actA.ID)
	if !errors.Is(err, store.ErrActivationLimit) {
		t.Fatalf("reactivate A returned %v, want ErrActivationLimit", err)
	}

	// Count must remain 1
	n, err := s.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 1 {
		t.Errorf("count = %d, want 1", n)
	}
}

func TestActivationLifecycle_TransferLicense(t *testing.T) {
	s := setupTestDB(t)
	defer s.Close()
	ctx := context.Background()

	lic := activationLicense(t, s, ctx, 2)

	// Activate two devices
	if err := s.ActivateWithinLimit(ctx, &model.Activation{
		LicenseID: lic.ID, Identifier: "dev-1", IdentifierType: "device",
	}, 2); err != nil {
		t.Fatalf("activate dev-1: %v", err)
	}
	if err := s.ActivateWithinLimit(ctx, &model.Activation{
		LicenseID: lic.ID, Identifier: "dev-2", IdentifierType: "device",
	}, 2); err != nil {
		t.Fatalf("activate dev-2: %v", err)
	}

	// Transfer to identical email must fail
	_, err := s.TransferLicense(ctx, lic.ID, lic.Email, true)
	if !errors.Is(err, store.ErrIdenticalEmail) {
		t.Fatalf("transfer to identical email returned %v, want ErrIdenticalEmail", err)
	}

	// Successful transfer with releaseActivations=true
	newEmail := "transferred-owner@example.com"
	released, err := s.TransferLicense(ctx, lic.ID, newEmail, true)
	if err != nil {
		t.Fatalf("transfer license: %v", err)
	}
	if released != 2 {
		t.Errorf("released = %d, want 2", released)
	}

	// Check license email was updated
	freshLic, err := s.FindLicenseByID(ctx, lic.ID)
	if err != nil {
		t.Fatalf("find license: %v", err)
	}
	if freshLic.Email != newEmail {
		t.Errorf("Email = %q, want %q", freshLic.Email, newEmail)
	}

	// Check active activation count is 0
	count, err := s.CountActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 0 {
		t.Errorf("active count = %d, want 0 after transfer with release", count)
	}

	// Both devices should now be soft-deactivated with reason 'transfer'
	acts, err := s.ListActivations(ctx, lic.ID)
	if err != nil {
		t.Fatalf("list activations: %v", err)
	}
	for _, a := range acts {
		if a.Status != model.ActivationDeactivated {
			t.Errorf("device %s status = %q, want deactivated", a.Identifier, a.Status)
		}
		if a.DeactivatedReason != "transfer" {
			t.Errorf("device %s reason = %q, want transfer", a.Identifier, a.DeactivatedReason)
		}
	}
}

func TestActivationLifecycle_MigrationAndRollback(t *testing.T) {
	s := setupTestDB(t)
	defer s.Close()
	ctx := context.Background()

	migrationsDir := "../../db/migrations"

	// 1. Rollback migration
	downSQL, err := os.ReadFile(filepath.Join(migrationsDir, "20261017_device_records.down.sql"))
	if err != nil {
		t.Fatalf("read down migration: %v", err)
	}
	if _, err := s.DB.ExecContext(ctx, string(downSQL)); err != nil {
		t.Fatalf("exec down migration: %v", err)
	}

	// Verify columns are dropped
	var colCount int
	err = s.DB.NewRaw(`
		SELECT count(*) FROM information_schema.columns
		WHERE table_name = 'activations'
		AND column_name IN ('scheme', 'status', 'deactivated_at', 'deactivated_reason')
	`).Scan(ctx, &colCount)
	if err != nil {
		t.Fatalf("check dropped columns: %v", err)
	}
	if colCount != 0 {
		t.Errorf("expected 0 columns after rollback, found %d", colCount)
	}

	// 2. Reapply migration
	upSQL, err := os.ReadFile(filepath.Join(migrationsDir, "20261017_device_records.up.sql"))
	if err != nil {
		t.Fatalf("read up migration: %v", err)
	}
	if _, err := s.DB.ExecContext(ctx, string(upSQL)); err != nil {
		t.Fatalf("exec up migration: %v", err)
	}

	// Verify columns are restored
	err = s.DB.NewRaw(`
		SELECT count(*) FROM information_schema.columns
		WHERE table_name = 'activations'
		AND column_name IN ('scheme', 'status', 'deactivated_at', 'deactivated_reason')
	`).Scan(ctx, &colCount)
	if err != nil {
		t.Fatalf("check restored columns: %v", err)
	}
	if colCount != 4 {
		t.Errorf("expected 4 columns after reapply, found %d", colCount)
	}
}
