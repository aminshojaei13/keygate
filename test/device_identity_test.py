#!/usr/bin/env python3
"""
Test runner for platform-independent device identity fixtures.
Validates normalization, domain-separated hashing, drift scoring,
cloned state simulation, and format validation against the specification.
"""

import json
import hashlib
import os
import sys

def normalize(val: str) -> str:
    # Strip control chars, trim whitespace, lowercase
    clean = "".join(c.lower() for c in val.strip() if not (ord(c) < 32 or ord(c) == 127))
    return clean

def hash_component(comp: str, raw: str) -> str:
    norm = normalize(raw)
    if not norm:
        return ""
    data = f"keygate:device:v1:{comp}:{norm}".encode("utf-8")
    return hashlib.sha256(data).hexdigest()

def compute_composite_digest(platform: str, comp_hashes: dict) -> str:
    norm_platform = (platform or "other").strip().lower()
    valid_keys = sorted([k.strip().lower() for k, v in comp_hashes.items() if v and v.strip()])
    if not valid_keys:
        raise ValueError("no valid component hashes")
    
    parts = []
    for k in valid_keys:
        parts.append(f"{k}={comp_hashes[k].strip().lower()}")
    payload = f"keygate:device:v1:composite:{norm_platform}:" + ";".join(parts)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()

def format_kgid(platform: str, digest: str) -> str:
    norm_platform = (platform or "other").strip().lower()
    return f"kgid:v1:{norm_platform}:{digest.strip().lower()}"

WEIGHTS = {
    "board": 35,
    "os": 25,
    "disk": 20,
    "cpu": 10,
    "nic": 10
}

def evaluate_drift(baseline: dict, incoming: dict) -> dict:
    matched_weight = 0
    changed_weight = 0
    missing_weight = 0
    
    matched = []
    changed = []
    missing = []
    
    for comp, weight in WEIGHTS.items():
        has_b = comp in baseline and baseline[comp].strip() != ""
        has_i = comp in incoming and incoming[comp].strip() != ""
        
        if has_b and has_i:
            if baseline[comp].strip().lower() == incoming[comp].strip().lower():
                matched_weight += weight
                matched.append(comp)
            else:
                changed_weight += weight
                changed.append(comp)
        elif has_b != has_i:
            missing_weight += weight
            missing.append(comp)
            
    comparable_weight = matched_weight + changed_weight
    if comparable_weight == 0 and missing_weight == 0:
        return {"score": 0, "decision": "reject_different_device"}
        
    total_weight = comparable_weight + (missing_weight * 0.5)
    score = int((matched_weight / total_weight) * 100)
    score = max(0, min(100, score))
    
    if score >= 60:
        decision = "accept_same_device"
    elif score >= 30:
        decision = "review_ambiguous"
    else:
        decision = "reject_different_device"
        
    return {
        "score": score,
        "decision": decision,
        "matched": sorted(matched),
        "changed": sorted(changed),
        "missing": sorted(missing)
    }

def run_tests():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    fixtures_path = os.path.join(script_dir, "fixtures", "device_identity_fixtures.json")
    
    with open(fixtures_path, "r", encoding="utf-8") as f:
        fixtures = json.load(f)
        
    print(f"Loaded fixtures: {fixtures_path}")
    
    # 1. Test Stable Identifiers
    print("\n[1] Testing Stable Identifiers...")
    darwin_fix = fixtures["stable_identifiers"][0]
    raw_comps = darwin_fix["raw_components"]
    computed_hashes = {}
    for comp, raw in raw_comps.items():
        h = hash_component(comp, raw)
        computed_hashes[comp] = h
        expected_h = darwin_fix["expected_component_hashes"][comp]
        assert h == expected_h, f"Hash mismatch for {comp}: {h} != {expected_h}"
    print("  ✓ All component hashes matched domain-separated SHA-256 specification")
    
    composite = compute_composite_digest(darwin_fix["platform"], computed_hashes)
    assert composite == darwin_fix["expected_composite_digest"], f"Composite mismatch: {composite}"
    kgid = format_kgid(darwin_fix["platform"], composite)
    assert kgid == darwin_fix["expected_kgid"], f"KGID mismatch: {kgid}"
    print(f"  ✓ Composite digest verified: {kgid}")

    # 2. Test Changed Characteristics
    print("\n[2] Testing Changed Characteristics Drift Scoring...")
    baseline_hashes = computed_hashes.copy()
    for case in fixtures["changed_characteristics"]:
        incoming = baseline_hashes.copy()
        for c in case["changed_components"]:
            incoming[c] = hash_component(c, f"CHANGED-{c}")
            
        res = evaluate_drift(baseline_hashes, incoming)
        expected_score = case["expected_drift_score"]
        expected_decision = case["expected_decision"]
        assert res["score"] == expected_score, f"Case {case['name']} score: {res['score']} != {expected_score}"
        assert res["decision"] == expected_decision, f"Case {case['name']} decision: {res['decision']} != {expected_decision}"
        print(f"  ✓ {case['name']}: score={res['score']} -> {res['decision']}")

    # 3. Test Cloned Client State
    print("\n[3] Testing Cloned Client State...")
    clone_fix = fixtures["cloned_client_state"]
    host_a = {k: hash_component(k, v) for k, v in clone_fix["host_a"].items()}
    host_b = {k: hash_component(k, v) for k, v in clone_fix["host_b_clone"].items()}
    res_clone = evaluate_drift(host_a, host_b)
    assert res_clone["score"] == clone_fix["expected_drift_score"], f"Clone score: {res_clone['score']}"
    assert res_clone["decision"] == clone_fix["expected_decision"], f"Clone decision: {res_clone['decision']}"
    print(f"  ✓ Cloned state successfully flagged as {res_clone['decision']} (score={res_clone['score']})")

    # 4. Test Missing Characteristics
    print("\n[4] Testing Missing Characteristics (Restricted Sandbox)...")
    for case in fixtures["missing_characteristics"]:
        base_h = {k: hash_component(k, v) for k, v in case["available_components"].items()}
        res_missing = evaluate_drift(base_h, base_h)
        assert res_missing["decision"] == case["expected_decision"], f"Missing check failed for {case['name']}"
        print(f"  ✓ {case['name']}: {res_missing['decision']} (score={res_missing['score']})")

    print("\n========================================================")
    print("ALL DEVICE IDENTITY FIXTURE TESTS PASSED SUCCESSFULLY! ✓")
    print("========================================================")

if __name__ == "__main__":
    run_tests()
