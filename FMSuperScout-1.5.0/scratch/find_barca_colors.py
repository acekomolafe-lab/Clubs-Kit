import re
import sys

path = r'C:\Users\aceik\AppData\Local\FMSuperScout\kit-scan.txt'
try:
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        text = f.read()
except Exception as e:
    print(f"Error reading file: {e}")
    sys.exit(1)

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

for c in clubs:
    h = c.splitlines()[0] if c.strip() else ""
    if "FC Barcelona" in h:
        print("=" * 60)
        print(h)
        try:
            sub = c.split("Cino Memory Dump:")[1].split("Pointer at Cino+0x78:")[0]
            raw = parse_hex_block(sub.splitlines())
            
            print(f"Length of raw: {len(raw)}")
            # Search for colors
            for off in range(0, min(len(raw), 768), 4):
                val = raw[off:off+4]
                if len(val) == 4:
                    if val[3] == 0xFF:
                        print(f"  +0x{off:03X}: {to_hex(val)} ({val.hex(' ')})")
        except Exception as e:
            print(f"Error parsing: {e}")
