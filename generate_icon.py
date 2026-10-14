import os
from PIL import Image, ImageDraw, ImageFilter

size = 512
img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

# 1. Fondo estilizado con esquinas redondeadas tipo app moderna
bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))
bg_draw = ImageDraw.Draw(bg)

margin = 28
r = 110
rect = [margin, margin, size - margin, size - margin]

mask = Image.new("L", (size, size), 0)
mask_draw = ImageDraw.Draw(mask)
mask_draw.rounded_rectangle(rect, radius=r, fill=255)

# Gradiente de fondo oscuro elegante (#0D1117 a #161F30)
for y in range(size):
    factor = y / size
    r_col = int(13 + (22 - 13) * factor)
    g_col = int(17 + (31 - 17) * factor)
    b_col = int(23 + (48 - 23) * factor)
    bg_draw.line([(0, y), (size, y)], fill=(r_col, g_col, b_col, 255))

base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
base.paste(bg, (0, 0), mask)

# Borde acentuado con resplandor cian/azul (#388BFD)
border_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
border_draw = ImageDraw.Draw(border_layer)
border_draw.rounded_rectangle(rect, radius=r, outline=(56, 139, 253, 220), width=6)
base = Image.alpha_composite(base, border_layer)

center_x, center_y = size // 2, size // 2

# Aro exterior de disco / progreso
draw_main = ImageDraw.Draw(base)
outer_ring = [105, 105, size - 105, size - 105]
draw_main.ellipse(outer_ring, outline=(25, 38, 58, 200), width=14)

# Arco de progreso circular brillante (#58A6FF)
draw_main.arc(outer_ring, start=-30, end=210, fill=(88, 166, 255, 255), width=14)
# Detalle verde éxito de restauración (#3FB950)
draw_main.arc(outer_ring, start=210, end=270, fill=(63, 185, 80, 255), width=14)

# Play / Film central
poly_points = [
    (center_x - 42, center_y - 75),
    (center_x + 75, center_y),
    (center_x - 42, center_y + 75)
]

# Sombra suave del play
shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
sh_draw = ImageDraw.Draw(shadow)
sh_poly = [(p[0] + 3, p[1] + 5) for p in poly_points]
sh_draw.polygon(sh_poly, fill=(0, 0, 0, 140))
shadow = shadow.filter(ImageFilter.GaussianBlur(8))
base = Image.alpha_composite(shadow, base)
draw_main = ImageDraw.Draw(base)

# Triángulo Play
draw_main.polygon(poly_points, fill=(88, 166, 255, 255))

# Onda / pulso de recuperación médica y rescate de video (#3FB950)
pulse_pts = [
    (115, center_y + 60),
    (170, center_y + 60),
    (200, center_y + 25),
    (230, center_y + 90),
    (260, center_y - 20),
    (290, center_y + 75),
    (320, center_y + 60),
    (395, center_y + 60)
]
draw_main.line(pulse_pts, fill=(63, 185, 80, 255), width=8, joint="curve")

os.makedirs("Resources", exist_ok=True)
ico_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
base.save(os.path.join("Resources", "app_icon.ico"), format="ICO", sizes=ico_sizes)
base.save(os.path.join("Resources", "app_icon.png"), format="PNG")
print("OK: Icono generado correctamente en Resources/app_icon.ico")
