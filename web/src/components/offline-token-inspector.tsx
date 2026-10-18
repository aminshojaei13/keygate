import { useQuery } from "@tanstack/react-query"
import { AlertCircle, CheckCircle2, Clock, Copy, Key, Shield, ShieldAlert, Sparkles } from "lucide-react"
import { useState } from "react"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Textarea } from "@/components/ui/textarea"
import { admin } from "@/lib/api"
import { parseOfflineToken } from "@/lib/offline-token"
import { formatDate } from "@/lib/utils"

export function OfflineTokenInspector({ initialToken }: { initialToken?: string }) {
  const [tokenInput, setTokenInput] = useState(initialToken || "")
  const [copied, setCopied] = useState(false)

  const { data: pubkeyData } = useQuery({
    queryKey: ["admin", "license-pubkey"],
    queryFn: admin.getLicensePublicKey,
    staleTime: 60 * 60 * 1000,
  })

  const parsed = tokenInput.trim() ? parseOfflineToken(tokenInput) : null
  const activeKid = pubkeyData?.kid

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <label className="text-sm font-medium text-foreground flex items-center justify-between">
          <span>Offline License Token String</span>
          {parsed && parsed.payload && (
            <span className="text-xs text-muted-foreground font-normal">
              Payload size: {parsed.payloadBase64?.length || 0} bytes
            </span>
          )}
        </label>
        <Textarea
          placeholder="Paste offline license token here (e.g. keygate.offline.v1.ey... or standard base64 envelope)..."
          value={tokenInput}
          onChange={(e) => setTokenInput(e.target.value)}
          rows={3}
          className="font-mono text-xs break-all"
        />
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>Standard format: <code>keygate.offline.v1.&lt;header&gt;.&lt;payload&gt;.&lt;sig&gt;</code></span>
          {tokenInput && (
            <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={() => setTokenInput("")}>
              Clear
            </Button>
          )}
        </div>
      </div>

      {!tokenInput.trim() && (
        <Card className="border-dashed bg-muted/30">
          <CardContent className="py-8 text-center text-muted-foreground">
            <Key className="h-8 w-8 mx-auto mb-2 opacity-50" />
            <p className="text-sm font-medium">No token provided</p>
            <p className="text-xs mt-1 max-w-md mx-auto">
              Paste an offline license token from a client application, SDK, or logs to inspect cryptographic claims,
              expiration terms, and device identity bindings.
            </p>
          </CardContent>
        </Card>
      )}

      {parsed && parsed.error && (
        <div className="rounded-lg border border-destructive/30 bg-destructive/10 p-4 text-destructive flex items-start gap-3">
          <ShieldAlert className="h-5 w-5 shrink-0 mt-0.5" />
          <div className="space-y-1">
            <p className="text-sm font-semibold">Invalid Offline Token Envelope</p>
            <p className="text-xs font-mono">{parsed.error}</p>
          </div>
        </div>
      )}

      {parsed && parsed.payload && (
        <div className="space-y-4">
          {/* Status summary banner */}
          <div className="rounded-lg border bg-card p-4 shadow-sm flex flex-wrap items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              {parsed.isExpired ? (
                <div className="h-9 w-9 rounded-full bg-amber-100 text-amber-700 flex items-center justify-center shrink-0">
                  <Clock className="h-5 w-5" />
                </div>
              ) : parsed.isTermExpired ? (
                <div className="h-9 w-9 rounded-full bg-red-100 text-red-700 flex items-center justify-center shrink-0">
                  <AlertCircle className="h-5 w-5" />
                </div>
              ) : (
                <div className="h-9 w-9 rounded-full bg-emerald-100 text-emerald-700 flex items-center justify-center shrink-0">
                  <CheckCircle2 className="h-5 w-5" />
                </div>
              )}
              <div>
                <div className="flex items-center gap-2">
                  <p className="font-semibold text-sm">
                    {parsed.isTermExpired
                      ? "License Term Expired"
                      : parsed.isExpired
                        ? "Offline Window Expired (Check-in Required)"
                        : "Cryptographically Structured Token"}
                  </p>
                  <Badge variant="outline" className="text-[10px] font-mono">
                    {parsed.format}
                  </Badge>
                </div>
                <p className="text-xs text-muted-foreground mt-0.5">
                  {parsed.offlineExpiresAt ? `Offline valid until ${formatDate(parsed.offlineExpiresAt.toISOString())}` : "Valid offline"}
                  {parsed.termExpiresAt ? ` · Term expires ${formatDate(parsed.termExpiresAt.toISOString())}` : " · Perpetual term"}
                </p>
              </div>
            </div>

            {/* Key ID comparison */}
            <div className="flex items-center gap-2">
              {parsed.header?.kid && (
                <Badge
                  variant={activeKid && parsed.header.kid === activeKid ? "secondary" : "outline"}
                  className="font-mono text-xs"
                >
                  kid: {parsed.header.kid}
                  {activeKid && parsed.header.kid === activeKid ? " (Active Server Key)" : " (Rotated / Other Key)"}
                </Badge>
              )}
            </div>
          </div>

          {/* Claims Grid */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <Card>
              <CardHeader className="py-3 px-4 border-b bg-muted/20">
                <CardTitle className="text-xs font-semibold uppercase tracking-wider text-muted-foreground flex items-center gap-2">
                  <Key className="h-3.5 w-3.5" />
                  License & Plan Claims
                </CardTitle>
              </CardHeader>
              <CardContent className="p-4 space-y-3 text-xs">
                <div className="flex justify-between items-center py-1 border-b border-muted">
                  <span className="text-muted-foreground">License ID:</span>
                  <span className="font-mono font-medium">{parsed.payload.lid}</span>
                </div>
                <div className="flex justify-between items-center py-1 border-b border-muted">
                  <span className="text-muted-foreground">Product ID:</span>
                  <span className="font-mono">{parsed.payload.pid}</span>
                </div>
                <div className="flex justify-between items-center py-1 border-b border-muted">
                  <span className="text-muted-foreground">Plan ID:</span>
                  <span className="font-mono">{parsed.payload.pln}</span>
                </div>
                <div className="flex justify-between items-center py-1 border-b border-muted">
                  <span className="text-muted-foreground">Status at issuance:</span>
                  <Badge variant="secondary" className="capitalize text-[10px]">
                    {parsed.payload.sts}
                  </Badge>
                </div>
                <div className="flex justify-between items-center py-1 border-b border-muted">
                  <span className="text-muted-foreground">Issued At:</span>
                  <span>{parsed.issuedAt ? formatDate(parsed.issuedAt.toISOString()) : "-"}</span>
                </div>
                <div className="flex justify-between items-center py-1">
                  <span className="text-muted-foreground">Grace Period:</span>
                  <span>{parsed.payload.grc ?? 0} days</span>
                </div>
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="py-3 px-4 border-b bg-muted/20">
                <CardTitle className="text-xs font-semibold uppercase tracking-wider text-muted-foreground flex items-center gap-2">
                  <Shield className="h-3.5 w-3.5" />
                  Device & Binding Claims
                </CardTitle>
              </CardHeader>
              <CardContent className="p-4 space-y-3 text-xs">
                <div className="space-y-1 py-1 border-b border-muted">
                  <span className="text-muted-foreground block">Bound Device Identifier (did):</span>
                  <code className="bg-muted px-2 py-1 rounded block text-[11px] font-mono break-all">
                    {parsed.payload.did || "None (Unbound)"}
                  </code>
                </div>
                {parsed.payload.fpr && (
                  <div className="space-y-1 py-1 border-b border-muted">
                    <span className="text-muted-foreground block">Device Fingerprint Hash (fpr):</span>
                    <code className="bg-muted px-2 py-1 rounded block text-[11px] font-mono break-all">
                      {parsed.payload.fpr}
                    </code>
                  </div>
                )}
                {parsed.payload.nce && (
                  <div className="flex justify-between items-center py-1">
                    <span className="text-muted-foreground">Anti-Replay Nonce:</span>
                    <span className="font-mono text-[11px]">{parsed.payload.nce}</span>
                  </div>
                )}
              </CardContent>
            </Card>
          </div>

          {/* Entitlements in token */}
          {parsed.payload.ftr && Object.keys(parsed.payload.ftr).length > 0 && (
            <Card>
              <CardHeader className="py-3 px-4 border-b bg-muted/20">
                <CardTitle className="text-xs font-semibold uppercase tracking-wider text-muted-foreground flex items-center gap-2">
                  <Sparkles className="h-3.5 w-3.5" />
                  Signed Feature Entitlements (ftr)
                </CardTitle>
              </CardHeader>
              <CardContent className="p-4">
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-2">
                  {Object.entries(parsed.payload.ftr).map(([feat, val]) => (
                    <div key={feat} className="rounded border bg-muted/30 px-3 py-2 text-xs flex justify-between items-center">
                      <span className="font-medium text-foreground">{feat}</span>
                      <Badge variant="outline" className="font-mono text-[10px]">
                        {String(val)}
                      </Badge>
                    </div>
                  ))}
                </div>
              </CardContent>
            </Card>
          )}

          {/* Signature Verification Details */}
          <div className="rounded-lg border bg-muted/20 p-3 text-xs space-y-2">
            <div className="flex items-center justify-between">
              <span className="font-semibold text-foreground flex items-center gap-1.5">
                <Shield className="h-3.5 w-3.5 text-primary" />
                Ed25519 Digital Signature
              </span>
              {parsed.signatureBase64 && (
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-6 text-xs gap-1"
                  onClick={() => copyToClipboard(parsed.signatureBase64!)}
                >
                  <Copy className="h-3 w-3" />
                  {copied ? "Copied" : "Copy Signature"}
                </Button>
              )}
            </div>
            <code className="bg-background px-2.5 py-1.5 rounded border block font-mono text-[11px] break-all">
              {parsed.signatureBase64 || "Missing signature"}
            </code>
            <p className="text-[11px] text-muted-foreground">
              Offline tokens are signed using the server's Ed25519 private seed. Desktop and offline clients verify this signature
              using the corresponding public key without network roundtrips.
            </p>
          </div>
        </div>
      )}
    </div>
  )
}
