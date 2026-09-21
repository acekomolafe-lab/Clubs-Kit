import json
with open('d:/Projects/FMSuperScout-1.5.0/FMSuperScout-1.5.0/scratch/user_clubs.json', 'r', encoding='utf-8') as f:
    data = json.load(f)
for c in data.get('clubs', []):
    name = c.get('name', '').lower()
    if 'east london' in name or c.get('id') == 2000000000: # just in case
        print(f"[{c.get('id')}] {c.get('name')} | Home: bg={c.get('k1BgColor')} fg={c.get('k1FgColor')} out={c.get('k1OutColor')} style={c.get('k1Style')} | Away: bg={c.get('k2BgColor')} fg={c.get('k2FgColor')} out={c.get('k2OutColor')} style={c.get('k2Style')}")
