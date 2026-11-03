"""Gera o ícone do app (assets/icone.ico e wwwroot/icone.png). Rode: python3 tools/gerar_icone.py"""
import os
from PIL import Image, ImageDraw

S = 1024  # desenha grande e reduz: as bordas ficam suaves
RAIZ = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "NfseApp")


def gradiente(w, h, topo, base):
    img = Image.new("RGBA", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / (h - 1)
        d.line([(0, y), (w, y)], fill=tuple(int(topo[i] + (base[i] - topo[i]) * t) for i in range(3)) + (255,))
    return img


def desenhar():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Fundo: quadrado arredondado azul em degradê
    mascara = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mascara).rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=255)
    img.paste(gradiente(S, S, (59, 130, 246), (30, 58, 138)), (0, 0), mascara)
    d = ImageDraw.Draw(img)

    # Folha da nota com o canto dobrado
    x0, y0, x1, y1, dobra = 268, 190, 716, 834, 150
    d.rounded_rectangle([x0, y0, x1, y1], radius=36, fill=(255, 255, 255, 255))
    d.polygon([(x1 - dobra, y0 - 1), (x1 + 1, y0 - 1), (x1 + 1, y0 + dobra)], fill=(0, 0, 0, 0))
    # devolve o fundo atrás do canto cortado
    canto = gradiente(S, S, (59, 130, 246), (30, 58, 138)).crop((x1 - dobra, y0 - 2, x1 + 2, y0 + dobra))
    tri = Image.new("L", canto.size, 0)
    ImageDraw.Draw(tri).polygon([(0, 0), (canto.size[0], 0), (canto.size[0], canto.size[1])], fill=255)
    img.paste(canto, (x1 - dobra, y0 - 2), tri)
    d.polygon([(x1 - dobra, y0), (x1 - dobra, y0 + dobra), (x1, y0 + dobra)], fill=(191, 209, 245, 255))

    # Linhas de texto
    cinza = (165, 180, 210, 255)
    for i, (y, fim) in enumerate([(400, 640), (480, 640), (560, 580), (640, 500)]):
        d.rounded_rectangle([336, y, fim, y + 36], radius=18, fill=cinza if i else (59, 130, 246, 255))

    # Selo verde de "autorizada"
    cx, cy, r = 704, 744, 176
    d.ellipse([cx - r - 26, cy - r - 26, cx + r + 26, cy + r + 26], fill=(255, 255, 255, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(22, 163, 74, 255))
    d.line([(cx - 82, cy + 4), (cx - 22, cy + 66), (cx + 92, cy - 62)], fill=(255, 255, 255, 255), width=46, joint="curve")
    for px, py in [(cx - 82, cy + 4), (cx + 92, cy - 62)]:
        d.ellipse([px - 23, py - 23, px + 23, py + 23], fill=(255, 255, 255, 255))
    return img


if __name__ == "__main__":
    grande = desenhar()
    grande.resize((256, 256), Image.LANCZOS).save(os.path.join(RAIZ, "wwwroot", "icone.png"))
    tamanhos = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    grande.resize((256, 256), Image.LANCZOS).save(os.path.join(RAIZ, "assets", "icone.ico"), sizes=tamanhos)
    print("ok")
