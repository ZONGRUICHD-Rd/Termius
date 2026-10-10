"""Generates the Conduit logo.

Outputs
  ios/Conduit/Resources/AppIcon.icon/   Icon Composer bundle (Liquid Glass layers, light/dark/tinted)
  windows/src/Conduit.Host/Assets/      conduit.png / conduit.ico (flattened render)
  tools/icon-preview.png

The mark: a thick "C" — the conduit between phone and computer — with a four-point spark in its mouth.
"""
import json, math, os, sys
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024
CX, CY, R, W = 492, 512, 268, 150          # arc centre, radius, stroke width
GAP = 62                                    # half-angle of the opening (degrees), facing right
SPARK = (742, 512, 132)                     # spark centre x, y, outer radius


def arc_path():
    """Filled outline of a thick arc with round caps (no SVG strokes, for renderer robustness)."""
    ro, ri, rc = R + W / 2, R - W / 2, W / 2
    a1, a2 = math.radians(GAP), math.radians(360 - GAP)
    def pt(r, a): return (CX + r * math.cos(a), CY - r * math.sin(a))
    o1, o2 = pt(ro, a1), pt(ro, a2)
    i1, i2 = pt(ri, a1), pt(ri, a2)
    f = lambda p: f"{p[0]:.2f} {p[1]:.2f}"
    return (f"M {f(o1)} A {ro:.2f} {ro:.2f} 0 1 0 {f(o2)} "
            f"A {rc:.2f} {rc:.2f} 0 0 0 {f(i2)} "
            f"A {ri:.2f} {ri:.2f} 0 1 1 {f(i1)} "
            f"A {rc:.2f} {rc:.2f} 0 0 0 {f(o1)} Z")


def spark_points(steps=48):
    x, y, r = SPARK
    pts = []
    for i in range(steps * 4):
        a = 2 * math.pi * i / (steps * 4)
        # astroid-like four-point star: concave sides
        c, s = math.cos(a), math.sin(a)
        k = (abs(c) ** (2 / 3) + abs(s) ** (2 / 3)) ** -1.5
        pts.append((x + r * c * k, y - r * s * k))
    return pts


def spark_path():
    pts = spark_points()
    return "M " + " L ".join(f"{px:.2f} {py:.2f}" for px, py in pts) + " Z"


def svg(path):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{S}" height="{S}" viewBox="0 0 {S} {S}">'
            f'<path d="{path}" fill="#FFFFFF"/></svg>\n')


def write_icon_bundle():
    out = os.path.join(ROOT, "ios", "Conduit", "Resources", "AppIcon.icon")
    os.makedirs(os.path.join(out, "Assets"), exist_ok=True)
    open(os.path.join(out, "Assets", "conduit.svg"), "w").write(svg(arc_path()))
    open(os.path.join(out, "Assets", "spark.svg"), "w").write(svg(spark_path()))
    clay = "srgb:0.85098,0.46667,0.34118,1.00000"
    icon = {
        "fill": {"linear-gradient": ["srgb:0.92157,0.58824,0.45098,1.00000", "srgb:0.72941,0.32157,0.20000,1.00000"],
                 "orientation": {"start": {"x": 0.5, "y": 0.0}, "stop": {"x": 0.5, "y": 1.0}}},
        "fill-specializations": [
            {"appearance": "dark", "value": {"linear-gradient": ["srgb:0.20784,0.18039,0.16863,1.00000",
                                                                   "srgb:0.10980,0.09804,0.09412,1.00000"]}},
        ],
        "groups": [
            {
                "name": "Spark",
                "layers": [{
                    "name": "Spark", "image-name": "spark.svg", "glass": True,
                    "fill-specializations": [{"appearance": "dark", "value": {"solid": clay}}],
                }],
                "lighting": "individual",
                "shadow": {"kind": "neutral", "opacity": 0.5},
                "translucency": {"enabled": True, "value": 0.35},
            },
            {
                "name": "Conduit",
                "layers": [{
                    "name": "Conduit", "image-name": "conduit.svg", "glass": True,
                    "fill-specializations": [{"appearance": "dark", "value": {"solid": "srgb:0.96078,0.95686,0.93333,1.00000"}}],
                }],
                "lighting": "combined",
                "shadow": {"kind": "neutral", "opacity": 0.45},
                "translucency": {"enabled": True, "value": 0.5},
            },
        ],
        "supported-platforms": {"squares": "shared", "circles": ["watchOS"]},
    }
    open(os.path.join(out, "icon.json"), "w").write(json.dumps(icon, indent=2) + "\n")


def render(size=S, dark=False):
    """Flat raster approximation of the glass icon (Windows icon, previews)."""
    top, bottom = ((53, 46, 43), (28, 25, 24)) if dark else ((235, 150, 115), (186, 82, 51))
    img = Image.new("RGBA", (size, size))
    d = ImageDraw.Draw(img)
    for yy in range(size):
        t = yy / size
        d.line([(0, yy), (size, yy)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    scale = 4
    big = Image.new("L", (S * scale, S * scale), 0)
    bd = ImageDraw.Draw(big)
    # arc
    ro, ri = (R + W / 2) * scale, (R - W / 2) * scale
    c = (CX * scale, CY * scale)
    bd.pieslice([c[0] - ro, c[1] - ro, c[0] + ro, c[1] + ro], GAP, 360 - GAP, fill=255)
    bd.ellipse([c[0] - ri, c[1] - ri, c[0] + ri, c[1] + ri], fill=0)
    for a in (GAP, -GAP):
        ex = c[0] + R * scale * math.cos(math.radians(a))
        ey = c[1] - R * scale * math.sin(math.radians(a))
        rr = W / 2 * scale
        bd.ellipse([ex - rr, ey - rr, ex + rr, ey + rr], fill=255)
    arc_mask = big.resize((size, size), Image.LANCZOS)
    big2 = Image.new("L", (S * scale, S * scale), 0)
    ImageDraw.Draw(big2).polygon([(x * scale, y * scale) for x, y in spark_points()], fill=255)
    spark_mask = big2.resize((size, size), Image.LANCZOS)
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sh_mask = Image.eval(Image.composite(arc_mask, spark_mask, arc_mask), lambda v: int(v * 0.35))
    shadow.putalpha(sh_mask.filter(ImageFilter.GaussianBlur(size / 60)))
    img = Image.alpha_composite(img, shadow.transform(img.size, Image.AFFINE, (1, 0, 0, 0, 1, -size / 80)))
    white = Image.new("RGBA", (size, size), (250, 249, 245, 255))
    clay = Image.new("RGBA", (size, size), (217, 119, 87, 255))
    img.paste(white, (0, 0), arc_mask)
    img.paste(clay if dark else white, (0, 0), spark_mask)
    return img


def main():
    write_icon_bundle()
    assets = os.path.join(ROOT, "windows", "src", "Conduit.Host", "Assets")
    flat = render()
    mask = Image.new("L", flat.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S, S], radius=230, fill=255)
    rounded = Image.new("RGBA", flat.size, (0, 0, 0, 0))
    rounded.paste(flat, (0, 0), mask)
    rounded.resize((256, 256), Image.LANCZOS).save(os.path.join(assets, "conduit.png"))
    rounded.save(os.path.join(assets, "conduit.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    preview = Image.new("RGBA", (S * 2 + 60, S), (255, 255, 255, 0))
    preview.paste(rounded, (0, 0))
    dark = render(dark=True)
    dr = Image.new("RGBA", dark.size, (0, 0, 0, 0)); dr.paste(dark, (0, 0), mask)
    preview.paste(dr, (S + 60, 0))
    preview.resize((S // 2 + 30, S // 4)).save(os.path.join(ROOT, "tools", "icon-preview.png"))


if __name__ == "__main__":
    main()
