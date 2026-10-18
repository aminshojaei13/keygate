import { Check, Copy, FileText, Search, ShieldAlert, ShieldCheck } from "lucide-react"
import { useMemo, useState } from "react"
import { showToast } from "@/components/toast"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent } from "@/components/ui/card"
import {
  DataTable,
  DataTableBody,
  DataTableCell,
  DataTableHead,
  DataTableHeader,
  DataTableRow,
} from "@/components/ui/data-table"
import { Input } from "@/components/ui/input"
import { Textarea } from "@/components/ui/textarea"

export interface ManifestFileEntry {
  path: string
  sha256: string
  size: number
}

export interface ParsedManifest {
  manifest_version: number
  product_id?: string
  release_id?: string
  version?: string
  channel?: string
  platform?: string
  arch?: string
  created_at?: string
  signing_key_id?: string
  files: ManifestFileEntry[]
}

export interface ManifestEnvelope {
  format?: string
  manifest_version?: number
  payload?: string
  signature?: string
  signing_key_id?: string
  algorithm?: string
  manifest?: ParsedManifest
}

function base64UrlDecode(str: string): string {
  let b64 = str.replace(/-/g, "+").replace(/_/g, "/")
  while (b64.length % 4) {
    b64 += "="
  }
  return decodeURIComponent(
    atob(b64)
      .split("")
      .map((c) => "%" + ("00" + c.charCodeAt(0).toString(16)).slice(-2))
      .join(""),
  )
}

function formatBytes(bytes?: number): string {
  if (!bytes || bytes <= 0) return "0 B"
  const k = 1024
  const sizes = ["B", "KB", "MB", "GB"]
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return `${(bytes / k ** i).toFixed(1)} ${sizes[i]}`
}

export function IntegrityManifestInspector({
  initialJson,
  knownKeyIds = [],
}: {
  initialJson?: string
  knownKeyIds?: string[]
}) {
  const [rawInput, setRawInput] = useState(initialJson || "")
  const [fileSearch, setFileSearch] = useState("")
  const [copiedHash, setCopiedHash] = useState<string | null>(null)

  const parsed = useMemo(() => {
    const trimmed = rawInput.trim()
    if (!trimmed) {
      return { status: "empty", error: null, manifest: null, envelope: null }
    }

    try {
      const parsedJson = JSON.parse(trimmed)

      // Case 1: SignedEnvelope (format: "keygate-integrity-v1")
      if (parsedJson.format === "keygate-integrity-v1" && parsedJson.payload) {
        let manifest: ParsedManifest
        if (parsedJson.manifest) {
          manifest = parsedJson.manifest
        } else {
          const decoded = base64UrlDecode(parsedJson.payload)
          manifest = JSON.parse(decoded)
        }

        const keyKnown = knownKeyIds.length === 0 || knownKeyIds.includes(parsedJson.signing_key_id)
        return {
          status: keyKnown ? "signed_envelope" : "unknown_key",
          envelope: parsedJson as ManifestEnvelope,
          manifest,
          error: null,
        }
      }

      // Case 2: Direct Manifest
      if (Array.isArray(parsedJson.files)) {
        return {
          status: "unverified_payload",
          envelope: null,
          manifest: parsedJson as ParsedManifest,
          error: null,
        }
      }

      return {
        status: "invalid",
        error: "JSON does not match keygate-integrity-v1 envelope or manifest structure (missing files array).",
        manifest: null,
        envelope: null,
      }
    } catch (e) {
      return {
        status: "invalid",
        error: e instanceof Error ? e.message : "Invalid JSON syntax",
        manifest: null,
        envelope: null,
      }
    }
  }, [rawInput, knownKeyIds])

  const filteredFiles = useMemo(() => {
    if (!parsed.manifest?.files) return []
    if (!fileSearch.trim()) return parsed.manifest.files
    const q = fileSearch.trim().toLowerCase()
    return parsed.manifest.files.filter(
      (f) => f.path.toLowerCase().includes(q) || f.sha256.toLowerCase().includes(q),
    )
  }, [parsed.manifest, fileSearch])

  const copyHash = (hash: string) => {
    navigator.clipboard.writeText(hash)
    setCopiedHash(hash)
    setTimeout(() => setCopiedHash(null), 2000)
    showToast("SHA-256 hash copied", "success")
  }

  const totalSize = useMemo(() => {
    return parsed.manifest?.files?.reduce((acc, f) => acc + (f.size || 0), 0) || 0
  }, [parsed.manifest])

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <label className="text-xs font-medium text-muted-foreground block">
          Paste integrity.manifest.json or Signed Envelope
        </label>
        <Textarea
          value={rawInput}
          onChange={(e) => setRawInput(e.target.value)}
          placeholder={`{\n  "format": "keygate-integrity-v1",\n  "manifest_version": 1,\n  "signing_key_id": "key_...",\n  "payload": "...",\n  "signature": "..."\n}`}
          rows={5}
          className="font-mono text-xs"
        />
      </div>

      {parsed.status === "invalid" && (
        <Card className="border-destructive/50 bg-destructive/10">
          <CardContent className="pt-4 pb-4 text-xs text-destructive flex items-center gap-2">
            <ShieldAlert className="h-4 w-4 shrink-0" />
            <span>{parsed.error}</span>
          </CardContent>
        </Card>
      )}

      {parsed.manifest && (
        <div className="space-y-4 border rounded-lg p-4 bg-muted/20">
          {/* Header Status */}
          <div className="flex flex-wrap items-center justify-between gap-2 border-b pb-3">
            <div className="flex items-center gap-2">
              {parsed.status === "signed_envelope" ? (
                <Badge className="bg-blue-100 text-blue-800 flex items-center gap-1">
                  <ShieldCheck className="h-3 w-3" />
                  Signed Envelope (Inspected)
                </Badge>
              ) : parsed.status === "unknown_key" ? (
                <Badge className="bg-amber-100 text-amber-800 flex items-center gap-1">
                  <ShieldAlert className="h-3 w-3" />
                  Key Not In Active Keyring
                </Badge>
              ) : (
                <Badge variant="outline" className="flex items-center gap-1">
                  <FileText className="h-3 w-3" />
                  Unsigned Manifest Payload
                </Badge>
              )}

              {parsed.envelope?.format && (
                <Badge variant="outline" className="font-mono text-[10px]">
                  {parsed.envelope.format}
                </Badge>
              )}
            </div>

            {parsed.envelope?.signing_key_id && (
              <span className="text-xs font-mono text-muted-foreground">
                kid: <strong className="text-foreground">{parsed.envelope.signing_key_id}</strong>
              </span>
            )}
          </div>

          {/* Metadata Cards */}
          <div className="grid grid-cols-2 gap-3 text-xs sm:grid-cols-4">
            <div>
              <span className="text-muted-foreground block">Version / Channel</span>
              <span className="font-medium">
                {parsed.manifest.version || "n/a"}{" "}
                {parsed.manifest.channel && `(${parsed.manifest.channel})`}
              </span>
            </div>
            <div>
              <span className="text-muted-foreground block">Platform / Arch</span>
              <span className="font-medium">
                {parsed.manifest.platform || "all"} / {parsed.manifest.arch || "all"}
              </span>
            </div>
            <div>
              <span className="text-muted-foreground block">Protected Files</span>
              <span className="font-medium">{parsed.manifest.files?.length || 0} files</span>
            </div>
            <div>
              <span className="text-muted-foreground block">Total Payload Size</span>
              <span className="font-medium">{formatBytes(totalSize)}</span>
            </div>
          </div>

          {/* Files Search */}
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted-foreground" />
            <Input
              value={fileSearch}
              onChange={(e) => setFileSearch(e.target.value)}
              placeholder="Search file path or hash in manifest..."
              className="pl-8 h-8 text-xs"
            />
          </div>

          {/* Files Table */}
          <div className="border rounded-md max-h-60 overflow-y-auto">
            <DataTable>
              <DataTableHeader>
                <DataTableRow>
                  <DataTableHead className="text-xs">File Path</DataTableHead>
                  <DataTableHead className="text-xs w-24">Size</DataTableHead>
                  <DataTableHead className="text-xs">SHA-256 Digest</DataTableHead>
                </DataTableRow>
              </DataTableHeader>
              <DataTableBody>
                {filteredFiles.map((file, idx) => (
                  <DataTableRow key={idx}>
                    <DataTableCell className="font-mono text-xs">{file.path}</DataTableCell>
                    <DataTableCell className="text-xs text-muted-foreground">
                      {formatBytes(file.size)}
                    </DataTableCell>
                    <DataTableCell>
                      <div className="flex items-center gap-1.5">
                        <code className="text-[11px] font-mono text-muted-foreground max-w-[24ch] truncate" title={file.sha256}>
                          {file.sha256}
                        </code>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="h-5 w-5"
                          onClick={() => copyHash(file.sha256)}
                        >
                          {copiedHash === file.sha256 ? (
                            <Check className="h-3 w-3 text-emerald-600" />
                          ) : (
                            <Copy className="h-3 w-3 text-muted-foreground" />
                          )}
                        </Button>
                      </div>
                    </DataTableCell>
                  </DataTableRow>
                ))}
              </DataTableBody>
            </DataTable>
          </div>
        </div>
      )}
    </div>
  )
}
