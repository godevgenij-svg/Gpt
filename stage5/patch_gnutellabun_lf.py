from pathlib import Path

root = Path(__file__).resolve().parents[1] / "gnutella"
codec = root / "src/protocol/codec.ts"
test_file = root / "tests/unit/protocol/content_addressing.test.ts"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one source block, found {count}")
    return text.replace(old, new, 1)


text = codec.read_text(encoding="utf-8")

needle = '''function parseQueryExtensions(rawExtensions: Buffer): {
'''
insert = '''function largeFileSizeFromGgepItems(items: GgepItem[]): number | undefined {
  const item = items.find((candidate) => candidate.id === "LF");
  if (!item) return undefined;
  const data = item.data;
  if (data.length < 1 || data.length > 8 || data[data.length - 1] === 0) {
    throw new Error("invalid GGEP LF payload");
  }
  let value = 0n;
  for (let i = 0; i < data.length; i++) {
    value |= BigInt(data[i] as number) << BigInt(i * 8);
  }
  if (value === 0n || value > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new Error("GGEP LF file size is outside the exact JavaScript integer range");
  }
  return Number(value);
}

function ggepLargeFileItem(fileSize: number): GgepItem | undefined {
  if (!Number.isSafeInteger(fileSize) || fileSize < 0) {
    throw new Error(`invalid query hit file size ${fileSize}`);
  }
  if (fileSize <= 0x7fffffff) return undefined;
  let value = BigInt(fileSize);
  const bytes: number[] = [];
  while (value > 0n) {
    bytes.push(Number(value & 0xffn));
    value >>= 8n;
  }
  return { id: "LF", data: Buffer.from(bytes) };
}

function parseQueryExtensions(rawExtensions: Buffer): {
'''
text = replace_once(text, needle, insert, "insert LF helpers")

old = '''function parseQueryHitExtension(rawExtension: Buffer): {
  urns: string[];
  metadata: string[];
} {
  const rawUrns: string[] = [];
  const metadata: string[] = [];
  const { textBlocks, ggepItems } =
    splitTextAndGgepExtensions(rawExtension);
  for (const block of textBlocks) {
    const text = block.toString("utf8");
    if (text.startsWith("urn:")) rawUrns.push(text);
    else if (text) metadata.push(text);
  }
  return {
    urns: normalizeUrnList([...rawUrns, ...urnsFromGgepItems(ggepItems)]),
    metadata,
  };
}
'''
new = '''function parseQueryHitExtension(rawExtension: Buffer): {
  urns: string[];
  metadata: string[];
  largeFileSize?: number;
} {
  const rawUrns: string[] = [];
  const metadata: string[] = [];
  const { textBlocks, ggepItems } =
    splitTextAndGgepExtensions(rawExtension);
  for (const block of textBlocks) {
    const text = block.toString("utf8");
    if (text.startsWith("urn:")) rawUrns.push(text);
    else if (text) metadata.push(text);
  }
  return {
    urns: normalizeUrnList([...rawUrns, ...urnsFromGgepItems(ggepItems)]),
    metadata,
    largeFileSize: largeFileSizeFromGgepItems(ggepItems),
  };
}
'''
text = replace_once(text, old, new, "parse query-hit extension")

old = '''  const fileIndex = payload.readUInt32LE(offset);
  const fileSize = payload.readUInt32LE(offset + 4);
  const nameEnd = queryHitFieldEnd(
    payload,
    offset + 8,
    tailStart,
    "file name",
  );
  const fileName = payload.subarray(offset + 8, nameEnd).toString("utf8");
  const extStart = nameEnd + 1;
  const extEnd = queryHitFieldEnd(
    payload,
    extStart,
    tailStart,
    "extension block",
  );
  const rawExtension = payload.subarray(extStart, extEnd);
  return {
    result: {
      fileIndex,
      fileSize,
      fileName,
      ...parseQueryHitExtension(rawExtension),
      rawExtension,
    },
'''
new = '''  const fileIndex = payload.readUInt32LE(offset);
  const legacyFileSize = payload.readUInt32LE(offset + 4);
  const nameEnd = queryHitFieldEnd(
    payload,
    offset + 8,
    tailStart,
    "file name",
  );
  const fileName = payload.subarray(offset + 8, nameEnd).toString("utf8");
  const extStart = nameEnd + 1;
  const extEnd = queryHitFieldEnd(
    payload,
    extStart,
    tailStart,
    "extension block",
  );
  const rawExtension = payload.subarray(extStart, extEnd);
  const extension = parseQueryHitExtension(rawExtension);
  return {
    result: {
      fileIndex,
      fileSize: extension.largeFileSize ?? legacyFileSize,
      fileName,
      urns: extension.urns,
      metadata: extension.metadata,
      rawExtension,
    },
'''
text = replace_once(text, old, new, "apply LF override")

old = '''    const item = Buffer.alloc(8);
    item.writeUInt32LE(r.index >>> 0, 0);
    item.writeUInt32LE(r.size >>> 0, 4);
    const textUrns = normalizeUrnList(r.sha1Urn ? [r.sha1Urn] : []);
    const ggepItems = ggepHashItemsForShare(
      r,
      textUrns,
      !!options.ggepHashes,
    );
'''
new = '''    const item = Buffer.alloc(8);
    item.writeUInt32LE(r.index >>> 0, 0);
    const largeFileItem = ggepLargeFileItem(r.size);
    item.writeUInt32LE(largeFileItem ? 0xffffffff : r.size >>> 0, 4);
    const textUrns = normalizeUrnList(r.sha1Urn ? [r.sha1Urn] : []);
    const ggepItems = [
      ...ggepHashItemsForShare(r, textUrns, !!options.ggepHashes),
      ...(largeFileItem ? [largeFileItem] : []),
    ];
'''
text = replace_once(text, old, new, "encode LF")
codec.write_text(text, encoding="utf-8", newline="\n")


tests = test_file.read_text(encoding="utf-8")
needle = '''  test("normalizes URN lists, extracts SHA-1 fallbacks, and decodes hashes", () => {
'''
insert = '''  test("encodes and decodes GGEP LF sizes above the legacy 32-bit limit", () => {
    const share = makeShare(7, "/tmp/large.iso", "large.iso");
    share.size = 5 * 1024 * 1024 * 1024;
    const payload = encodeQueryHit(
      6346,
      "1.2.3.4",
      512,
      [share],
      Buffer.alloc(16, 0x66),
      { ggepHashes: true },
    );

    const parsed = parseQueryHit(payload);

    expect(payload.readUInt32LE(15)).toBe(0xffffffff);
    expect(parsed.results).toHaveLength(1);
    expect(parsed.results[0]?.fileSize).toBe(share.size);
  });

  test("normalizes URN lists, extracts SHA-1 fallbacks, and decodes hashes", () => {
'''
tests = replace_once(tests, needle, insert, "insert LF unit test")
test_file.write_text(tests, encoding="utf-8", newline="\n")

print("Applied strict GGEP LF query-hit patch and unit test")
