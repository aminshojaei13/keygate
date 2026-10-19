import { useQuery } from "@tanstack/react-query"
import {
  BookOpen,
  Check,
  Code2,
  Copy,
  KeyRound,
  Shield,
  ShieldAlert,
  Terminal,
} from "lucide-react"
import { useState } from "react"
import { OfflineTokenInspector } from "@/components/offline-token-inspector"
import { showToast } from "@/components/toast"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { useI18n } from "@/i18n"
import { admin } from "@/lib/api"

export default function IntegrationPage() {
  const { t } = useI18n()
  const [copiedKey, setCopiedKey] = useState(false)
  const [copiedSnippet, setCopiedSnippet] = useState<string | null>(null)

  const { data: pubKeyData } = useQuery({
    queryKey: ["admin", "license-public-key"],
    queryFn: admin.getLicensePublicKey,
  })

  const copyToClipboard = (text: string, id: string) => {
    navigator.clipboard.writeText(text)
    if (id === "pubkey") {
      setCopiedKey(true)
      setTimeout(() => setCopiedKey(false), 2000)
    } else {
      setCopiedSnippet(id)
      setTimeout(() => setCopiedSnippet(null), 2000)
    }
    showToast("Copied to clipboard", "success")
  }

  const pythonActivationSnippet = `import requests
import hashlib
import platform

BASE_URL = "http://localhost:9000" # Replace with your Keygate instance URL
LICENSE_KEY = "KG-XXXX-XXXX-XXXX"

# Generate deterministic device fingerprint (sha256-v1 scheme)
machine_id = f"{platform.node()}-{platform.machine()}"
fingerprint = "sha256:" + hashlib.sha256(machine_id.encode()).hexdigest()

# 1. Activate device
response = requests.post(f"{BASE_URL}/api/v1/license/activate", json={
    "license_key": LICENSE_KEY,
    "identifier": fingerprint,
    "identifier_type": "device",
    "scheme": "sha256-v1",
    "label": platform.node()
})

data = response.json()
if response.status_code == 200:
    print("Activated successfully!")
    token = data.get("token")
    # Save token locally for offline operations
else:
    print(f"Activation failed: {data.get('error')}")
`

  const csharpSnippet = `// KeygateClient.cs - .NET / C# Windows Desktop Client Integration
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

public class KeygateClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _licenseKey;

    public KeygateClient(string baseUrl, string licenseKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _licenseKey = licenseKey;
        _http = new HttpClient();
    }

    /// <summary>
    /// Generates a deterministic SHA-256 device fingerprint from Windows MachineGuid.
    /// Matches the server's 'sha256-v1' scheme.
    /// </summary>
    public static string GetWindowsFingerprint()
    {
        string rawId = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\\Microsoft\\Cryptography");
            rawId = key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { }

        if (string.IsNullOrEmpty(rawId))
            rawId = Environment.MachineName + "-" + Environment.UserName;

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(rawId));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 1. Online Activation: POST /api/v1/license/activate
    /// </summary>
    public async Task<string> ActivateAsync()
    {
        string fingerprint = GetWindowsFingerprint();
        var payload = new
        {
            license_key = _licenseKey,
            identifier = fingerprint,
            identifier_type = "device",
            scheme = "sha256-v1",
            label = Environment.MachineName
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_baseUrl}/api/v1/license/activate", content);
        string json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Activation failed ({response.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        string token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
        return token;
    }

    /// <summary>
    /// 2. Heartbeat / Verify: POST /api/v1/license/verify
    /// </summary>
    public async Task<bool> VerifyOnlineAsync()
    {
        string fingerprint = GetWindowsFingerprint();
        var payload = new
        {
            license_key = _licenseKey,
            identifier = fingerprint
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_baseUrl}/api/v1/license/verify", content);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// 3. Release Slot: POST /api/v1/license/deactivate
    /// </summary>
    public async Task DeactivateAsync()
    {
        string fingerprint = GetWindowsFingerprint();
        var payload = new
        {
            license_key = _licenseKey,
            identifier = fingerprint
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        await _http.PostAsync($"{_baseUrl}/api/v1/license/deactivate", content);
    }
}
`

  const pythonOfflineVerifySnippet = `import base64
import json
from cryptography.hazmat.primitives.asymmetric import ed25519

# 1. Fetch public key from Keygate instance or use embedded key
PUBKEY_BASE64 = "${pubKeyData?.public_key || "YOUR_SERVER_ED25519_PUBLIC_KEY"}"
public_key_bytes = base64.b64decode(PUBKEY_BASE64)
verify_key = ed25519.Ed25519PublicKey.from_public_bytes(public_key_bytes)

def verify_offline_token(token_str: str) -> dict:
    parts = token_str.strip().split(".")
    # Payload is second-to-last segment, signature is last segment
    payload_b64, sig_b64 = parts[-2], parts[-1]
    
    # Signature was generated over ASCII bytes of payload_b64
    sig_bytes = base64.urlsafe_b64decode(sig_b64 + "==")
    verify_key.verify(sig_bytes, payload_b64.encode("ascii"))
    
    payload_bytes = base64.urlsafe_b64decode(payload_b64 + "==")
    claims = json.loads(payload_bytes.decode())
    print("Cryptographic signature valid! Claims:", claims)
    return claims
`

  const curlActivationSnippet = `# Online Activation
curl -X POST http://localhost:9000/api/v1/license/activate \\
  -H "Content-Type: application/json" \\
  -d '{
    "license_key": "KG-XXXX-XXXX-XXXX",
    "identifier": "sha256:4f8e6c...d2b",
    "identifier_type": "device",
    "scheme": "sha256-v1",
    "label": "Workstation-1"
  }'

# Online Verification (Heartbeat)
curl -X POST http://localhost:9000/api/v1/license/verify \\
  -H "Content-Type: application/json" \\
  -d '{
    "license_key": "KG-XXXX-XXXX-XXXX",
    "identifier": "sha256:4f8e6c...d2b"
  }'

# Release / Deactivate Device
curl -X POST http://localhost:9000/api/v1/license/deactivate \\
  -H "Content-Type: application/json" \\
  -d '{
    "license_key": "KG-XXXX-XXXX-XXXX",
    "identifier": "sha256:4f8e6c...d2b"
  }'

# Fetch Ed25519 Public Key
curl http://localhost:9000/api/v1/license/pubkey
`

  const tauriMinisignSnippet = `// In tauri.conf.json -> plugins -> updater
{
  "endpoints": [
    "http://localhost:8080/api/v1/updates/{{target}}/{{current_version}}"
  ],
  "pubkey": "YOUR_TAURI_MINISIGN_PUBLIC_KEY"
}
`

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">{t("integration.title")}</h1>
        <p className="text-muted-foreground">{t("integration.subtitle")}</p>
      </div>

      {/* Security Boundary Notice */}
      <Card className="border-amber-400 bg-amber-100/80 dark:border-amber-600/70 dark:bg-amber-900/30">
        <CardContent className="pt-6">
          <div className="flex gap-3 items-start">
            <ShieldAlert className="h-5 w-5 text-amber-600 shrink-0 mt-0.5" />
            <div className="space-y-1 text-sm text-amber-900 dark:text-amber-200">
              <p className="font-semibold">Security Boundary & Cryptographic Verification Notice</p>
              <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                {t("integration.securityNotice")}
              </p>
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Main Tabs */}
      <Tabs defaultValue="inspector" className="space-y-6">
        <TabsList className="flex flex-wrap h-auto p-1">
          <TabsTrigger value="inspector">{t("integration.inspectorTitle")}</TabsTrigger>
          <TabsTrigger value="pubkey">Public Key & Cryptography</TabsTrigger>
          <TabsTrigger value="csharp">C# (.NET / Windows)</TabsTrigger>
          <TabsTrigger value="online">Online Activation (Python/cURL)</TabsTrigger>
          <TabsTrigger value="offline">Offline Token Verification</TabsTrigger>
          <TabsTrigger value="releases">Release Integrity</TabsTrigger>
        </TabsList>

        {/* Tab 1: Token Inspector */}
        <TabsContent value="inspector" className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>{t("integration.inspectorTitle")}</CardTitle>
              <CardDescription>{t("integration.inspectorDesc")}</CardDescription>
            </CardHeader>
            <CardContent>
              <OfflineTokenInspector />
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab 2: Public Key & Cryptography */}
        <TabsContent value="pubkey" className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <KeyRound className="h-5 w-5 text-primary" />
                Active Ed25519 License Token Public Key
              </CardTitle>
              <CardDescription>
                Public key exposed via <code>/license/pubkey</code> for local offline token verification.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid grid-cols-1 gap-4 text-sm sm:grid-cols-3">
                <div className="border rounded p-3 bg-muted/30">
                  <span className="text-xs text-muted-foreground block">Algorithm</span>
                  <span className="font-mono font-semibold">{pubKeyData?.algorithm || "Ed25519"}</span>
                </div>
                <div className="border rounded p-3 bg-muted/30">
                  <span className="text-xs text-muted-foreground block">Key ID (kid)</span>
                  <span className="font-mono font-semibold truncate block" title={pubKeyData?.kid || "default"}>
                    {pubKeyData?.kid || "default"}
                  </span>
                </div>
                <div className="border rounded p-3 bg-muted/30">
                  <span className="text-xs text-muted-foreground block">Format</span>
                  <span className="font-medium">{pubKeyData?.format || "RFC 8037 JWK / Ed25519"}</span>
                </div>
              </div>

              {pubKeyData?.public_key && (
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-xs font-medium">Public Key (Base64)</span>
                    <Button
                      variant="outline"
                      size="sm"
                      className="h-7 text-xs"
                      onClick={() => copyToClipboard(pubKeyData.public_key!, "pubkey")}
                    >
                      {copiedKey ? <Check className="h-3 w-3 mr-1 text-emerald-600" /> : <Copy className="h-3 w-3 mr-1" />}
                      {copiedKey ? "Copied" : "Copy Public Key"}
                    </Button>
                  </div>
                  <pre className="text-xs font-mono bg-muted p-3 rounded-lg overflow-x-auto break-all select-all">
                    {pubKeyData.public_key}
                  </pre>
                </div>
              )}

              <div className="border rounded-lg p-4 bg-muted/30 space-y-2 text-xs text-muted-foreground">
                <p className="font-medium text-foreground">API Endpoints:</p>
                <ul className="list-disc pl-5 space-y-1 font-mono text-[11px]">
                  <li><code>GET /api/v1/license/pubkey</code> - Public Ed25519 verification key and metadata</li>
                  <li><code>POST /api/v1/license/activate</code> - Bind device and receive signed offline token</li>
                  <li><code>POST /api/v1/license/verify</code> - Verify device heartbeat and refresh offline token</li>
                  <li><code>POST /api/v1/license/deactivate</code> - Gracefully release device activation slot</li>
                </ul>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab: C# (.NET / Windows) */}
        <TabsContent value="csharp" className="space-y-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <Code2 className="h-4 w-4" />
                    .NET / C# Windows Client Integration
                  </CardTitle>
                  <CardDescription>
                    Hardware fingerprinting (MachineGuid), online activation, heartbeat, and slot release
                  </CardDescription>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => copyToClipboard(csharpSnippet, "csharp-code")}
                >
                  {copiedSnippet === "csharp-code" ? (
                    <Check className="h-3 w-3 mr-1 text-emerald-600" />
                  ) : (
                    <Copy className="h-3 w-3 mr-1" />
                  )}
                  Copy C# Code
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <pre className="text-xs font-mono bg-muted p-4 rounded-lg overflow-x-auto select-all">
                {csharpSnippet}
              </pre>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab: Online Activation (Python/cURL) */}
        <TabsContent value="online" className="space-y-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <Code2 className="h-4 w-4" />
                    Python Client Integration
                  </CardTitle>
                  <CardDescription>Activate machine and capture offline token</CardDescription>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => copyToClipboard(pythonActivationSnippet, "python-act")}
                >
                  {copiedSnippet === "python-act" ? <Check className="h-3 w-3 mr-1 text-emerald-600" /> : <Copy className="h-3 w-3 mr-1" />}
                  Copy Code
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <pre className="text-xs font-mono bg-muted p-4 rounded-lg overflow-x-auto select-all">
                {pythonActivationSnippet}
              </pre>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <Terminal className="h-4 w-4" />
                    cURL Command Examples
                  </CardTitle>
                  <CardDescription>Direct HTTP API calls for shell scripts and testing</CardDescription>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => copyToClipboard(curlActivationSnippet, "curl-act")}
                >
                  {copiedSnippet === "curl-act" ? <Check className="h-3 w-3 mr-1 text-emerald-600" /> : <Copy className="h-3 w-3 mr-1" />}
                  Copy Commands
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <pre className="text-xs font-mono bg-muted p-4 rounded-lg overflow-x-auto select-all">
                {curlActivationSnippet}
              </pre>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab 4: Offline Verification */}
        <TabsContent value="offline" className="space-y-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <Shield className="h-4 w-4" />
                    Python Offline Ed25519 Signature Verification
                  </CardTitle>
                  <CardDescription>Verify offline tokens locally without contacting the server</CardDescription>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => copyToClipboard(pythonOfflineVerifySnippet, "py-offline")}
                >
                  {copiedSnippet === "py-offline" ? <Check className="h-3 w-3 mr-1 text-emerald-600" /> : <Copy className="h-3 w-3 mr-1" />}
                  Copy Code
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <pre className="text-xs font-mono bg-muted p-4 rounded-lg overflow-x-auto select-all">
                {pythonOfflineVerifySnippet}
              </pre>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab 5: Release Integrity */}
        <TabsContent value="releases" className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <BookOpen className="h-4 w-4" />
                Tauri and Sparkle Release Verification
              </CardTitle>
              <CardDescription>
                Configure updater manifests to verify release artifact signatures using Minisign and Ed25519.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <pre className="text-xs font-mono bg-muted p-4 rounded-lg overflow-x-auto select-all">
                {tauriMinisignSnippet}
              </pre>
              <p className="text-xs text-muted-foreground">
                Release artifacts published in Keygate have their SHA-256 hashes and cryptographic signatures
                recorded immutably. Client updaters must verify hashes and signatures against trusted keys before
                executing update payloads.
              </p>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  )
}
