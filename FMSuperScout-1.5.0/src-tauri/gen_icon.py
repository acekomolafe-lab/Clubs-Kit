from PIL import Image

# Create a 32x32 transparent image
img = Image.new('RGBA', (32, 32), (0, 0, 0, 0))
img.save('icons/icon.ico', format='ICO', sizes=[(32, 32)])
