package service

import "testing"

func TestStrictSchemeSupported(t *testing.T) {
	cases := []struct {
		name       string
		identifier string
		want       bool
	}{
		{
			name:       "kgid is hardware derived",
			identifier: "kgid:v1:windows:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7",
			want:       true,
		},
		{
			name:       "legacy monolithic hardware hash",
			identifier: "sha256:5581b5a7b290e096fdea9b19916561e13d15fc983a9b57c53a9e5ad91f29f4b7",
			want:       true,
		},
		{
			name:       "random uuid cannot detect changes",
			identifier: "a0b1c2d3-e4f5-4a6b-8c9d-0e1f2a3b4c5d",
			want:       false,
		},
		{
			name:       "opaque free form identifier",
			identifier: "my-machine",
			want:       false,
		},
		{
			name:       "malformed kgid",
			identifier: "kgid:v1:windows:not-a-digest",
			want:       false,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := strictSchemeSupported(tc.identifier); got != tc.want {
				t.Errorf("strictSchemeSupported(%q) = %v, want %v", tc.identifier, got, tc.want)
			}
		})
	}
}
