import re

def parse_dump(filename):
    with open(filename, 'r') as f:
        lines = f.readlines()
        
    teams = {}
    current_team = None
    
    for line in lines:
        m_team = re.search(r'=== (.*?) \(ref: (.*?), UID: \d+\) ===', line)
        if m_team:
            current_team = m_team.group(2)
            teams[current_team] = {'lines': [], 'detected': None}
            continue
            
        m_det = re.search(r'Detected Kit Colors: Primary: (#[0-9A-Fa-f]{6}), Secondary: (#[0-9A-Fa-f]{6})', line)
        if m_det and current_team:
            teams[current_team]['detected'] = m_det.groups()
            continue

        m_hex = re.search(r'^\s+\+0x([0-9A-F]+):\s+((?:[0-9A-F]{2} )+)', line)
        if current_team and m_hex:
            offset = int(m_hex.group(1), 16)
            hex_bytes = m_hex.group(2).strip().split(' ')
            teams[current_team]['lines'].append((offset, [int(b, 16) for b in hex_bytes]))
            
    return teams

def hex_to_bgra(hex_str):
    hex_str = hex_str.lstrip('#')
    r, g, b = int(hex_str[0:2], 16), int(hex_str[2:4], 16), int(hex_str[4:6], 16)
    return [b, g, r, 255]

teams = parse_dump('kit-scan.txt')

for team, data in teams.items():
    if not data['detected']: continue
    
    prim_bgra = hex_to_bgra(data['detected'][0])
    sec_bgra = hex_to_bgra(data['detected'][1])
    
    print(f"Team: {team}")
    print(f"  Detected: {data['detected'][0]} (BGRA: {prim_bgra}), {data['detected'][1]} (BGRA: {sec_bgra})")
    
    dump_bytes = []
    for offset, b in data['lines']:
        dump_bytes.extend(b)
        
    for i in range(len(dump_bytes) - 3):
        color = dump_bytes[i:i+4]
        if color == prim_bgra:
            print(f"  Found primary detected at offset 0x{i:X}")
        if color == sec_bgra:
            print(f"  Found secondary detected at offset 0x{i:X}")
