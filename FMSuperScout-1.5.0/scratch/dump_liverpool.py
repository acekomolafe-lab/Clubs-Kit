import re, sys

path = r'C:\Users\aceik\AppData\Local\FMSuperScout\kit-scan.txt'
with open(path, 'r', encoding='utf-8', errors='ignore') as f:
    text = f.read()

clubs = re.split(r'\n(?====\s+)', text)

def parse_hex_block(lines):
    data = bytearray()
    for line in lines:
        m = re.search(r'\+0x[0-9a-fA-FX]+:\s+((?:[0-9A-Fa-f]{2}\s+){1,16})', line)
        if m:
            data.extend(bytes.fromhex(m.group(1).strip()))
    return data

def to_hex(b4):
    b, g, r, a = b4
    return f"#{r:02X}{g:02X}{b:02X}"

offset_counts = {}
for c in clubs:
    h = c.splitlines()[0] if c.strip() else ""
    if "UID:" in h:
        try:
            sub = c.split("Cino Memory Dump:")[1].split("Pointer at Cino+0x78:")[0]
            raw = parse_hex_block(sub.splitlines())
            for off in range(0x90, 0xB0, 4):
                val = raw[off:off+4]
                if len(val) == 4 and val[3] == 0xFF:
                    offset_counts[off] = offset_counts.get(off, 0) + 1
        except Exception as e:
            pass

for off, cnt in sorted(offset_counts.items()):
    print(f"Offset 0x{off:03X} has {cnt} valid colors")
