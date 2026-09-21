import re
with open('d:/Projects/FMSuperScout-1.5.0/FMSuperScout-1.5.0/scratch/user_debug.txt', 'r', encoding='utf-8') as f:
    text = f.read()

# Look for blocks that have either #E81860 or #D8E028
blocks = text.split('[CINO ')
for b in blocks:
    if '#E81860' in b or '#D8E028' in b:
        print('[CINO ' + b.strip())
