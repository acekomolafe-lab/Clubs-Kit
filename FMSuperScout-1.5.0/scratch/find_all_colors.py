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

print(f"Found {len(clubs)} club sections.")
for c in clubs:
    h = c.splitlines()[0] if c.strip() else ""
    if not h: continue
    print("=" * 60)
    print(h)
    
    # Try to find expected colors in header
    expected = []
    exp_m = re.search(r'Expected Kit Colors:\s+(#[0-9A-F]{6})\s*/\s*(#[0-9A-F]{6})', c)
    if exp_m:
        expected = [exp_m.group(1), exp_m.group(2)]
        print(f"  Expected: {expected}")
        
    try:
        sub = c.split("Cino Memory Dump:")[1].split("Pointer at Cino+0x78:")[0]
        raw = parse_hex_block(sub.splitlines())
        
        found = False
        for off in range(0, min(len(raw), 768), 4):
            val = raw[off:off+4]
            if len(val) == 4 and val[3] == 0xFF:
                hx = to_hex(val)
                if hx in expected or hx in ["#FFFFFF", "#000000", "#EF0107", "#D2122E", "#C1272D", "#ED1C24", "#A50044", "#004D98", "#FEBE10", "#DA291C", "#FFE500", "#C8102E", "#00B2A9", "#DC052D", "#0066B2", "#FDE100", "#018749", "#1B458F"]:
                    print(f"  +0x{off:03X}: {hx} ({val.hex(' ')})")
                    found = True
        
        if not found:
            # Just print the 0x80 to 0xC0 range to inspect manually
            for off in range(0x80, min(len(raw), 0xE0), 4):
                val = raw[off:off+4]
                if len(val) == 4 and val[3] == 0xFF:
                    print(f"  +0x{off:03X}: {to_hex(val)}")
                
    except Exception as e:
        pass
