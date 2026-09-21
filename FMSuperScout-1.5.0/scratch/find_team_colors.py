import re
import sys

path = r'C:\Users\aceik\AppData\Local\FMSuperScout\kit-scan.txt'
try:
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        text = f.read()
except Exception as e:
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

count = 0
for c in clubs:
    h = c.splitlines()[0] if c.strip() else ""
    if not "UID:" in h:
        continue
    try:
        sub = c.split("Cino Memory Dump:")[1].split("Pointer at Cino+0x78:")[0]
        raw = parse_hex_block(sub.splitlines())
        if len(raw) < 0xB0: continue
        
        # Check all offsets between 0x90 and 0xB0 for valid colors (alpha == 0xFF)
        # We already know 0xA0 and 0xA8 are Trim and Base.
        found_other = False
        for off in range(0x90, 0xB0, 4):
            if off in (0xA0, 0xA8): continue
            val = raw[off:off+4]
            if len(val) == 4 and val[3] == 0xFF:
                found_other = True
        
        if found_other:
            print("=" * 60)
            print(h)
            for off in range(0x90, 0x100, 4):
                val = raw[off:off+4]
                if len(val) == 4 and val[3] == 0xFF:
                    print(f"  +0x{off:03X}: {to_hex(val)} ({val.hex(' ')})")
            count += 1
            if count >= 10:
                break
    except Exception as e:
        pass
