"""MemoryBlackHole — 图标生成器（紫环 + 黑心 + 暖黄星点 + 倾斜金环）

方法与 aicbbuu-network-tools 的 tools/make_icon.py 一致：全部由几何与
距离场计算生成，4x4 超采样 + 盒式降采样求均值，因此任意尺寸都清晰，
不使用第三方图形库、现成素材或字体渲染，不涉及图标库授权。

**方向以原 AppIcon.ico 为准**：金环长轴「左下 -> 右上」倾斜。界面里
原先用 XAML 的<RotateTransform Angle="-18"/>画，那是逆时针，和 EXE
图标正好相反——两套独立实现导致的偏差。这次统一到同一份生成结果。

配色取自 MainWindow.xaml 的 AccentGradient 与原图标的实际取色：

  环：外缘 #7564F7 -> 内缘 #BBA7FF（线性，左上亮、右下深）
  心：#151820（带一点紫，纯黑在深色背景上会和底色糊掉）
  点：#FFE7A6 暖黄
  环：#FFF0C2 倾斜金环

输出：7 种尺寸的 PNG + 一个 6 档ICO（16/24/32/48/64/256）
"""
from __future__ import annotations

import math
import os
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "MemoryBlackHole" / "Assets"
DOCS_DIR = ROOT / "docs"
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(DOCS_DIR, exist_ok=True)

# ---------------------------------------------------------------- #
#  配色
# ---------------------------------------------------------------- #
# 环：StartPoint(0,0) -> EndPoint(1,1) 的线性渐变，即左上亮、右下深。
# 渐变三色的**权重**按原图标实际观感调过：青只出现在右下角一小段，
# 整体仍是紫。Stop 写0.48/0.48 会让青色占掉右下整整一半，图标发蓝。
RING_HI = (187, 167, 255)      # #BBA7FF  左上
RING_MID = (117, 100, 247)     # #7564F7  中段
RING_LO = (146, 118, 252)      # 收尾仍偏紫，不让青 dominate

CORE_HI = (26, 28, 40)         # 圆心受光处
CORE = (21, 24, 32)            # #151820 圆心本色

STAR = (255, 231, 166)         # #FFE7A6 中心暖光点
RING_GOLD = (255, 240, 194)    # #FFF0C2 倾斜金环

R_OUT = 0.485                  # 外圆半径（0.5留抗锯齿余量）
RING_W = 0.132        # 环的径向厚度

# 倾斜金环：与原图标一致——长轴「左下 -> 右上」，即数学上正角度为顺时针。
# XAML 的 RotateTransform 负角度是逆时针，两边正好差180 度。
ORBIT_DEG = 18.0               # 顺时针 18 度
ORBIT_RX = 0.368              # 椭圆长半轴
ORBIT_RY = 0.135               # 椭圆短半轴（要够大，金环才穿过中心点）
ORBIT_W = 0.0086               # 环线宽（原图标是细弧，粗了像皮带）
ORBIT_GAP = 0.042              # 金环与圆心的间隙：金环从圆心外缘之外掠过

STAR_R = 0.058        # 中心暖光点半径
STAR_SOFT = 0.100              # 暖光晕到这里的强度归零

SIZES = (16, 24, 32, 48, 64, 128, 256)


def _lerp(a, b, t):
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    return (int(round(a[0] + (b[0] - a[0]) * t)),
            int(round(a[1] + (b[1] - a[1]) * t)),
            int(round(a[2] + (b[2] - a[2]) * t)))


def _grad(t: float):
    """三段渐变：#BBA7FF -> #7564F7 -> #39D7FF。t: 0=左上 -> 1=右下"""
    if t < 0.72:
        return _lerp(RING_HI, RING_MID, t / 0.72)
    return _lerp(RING_MID, RING_LO, (t - 0.72) / 0.28)


def _render(size: int, simple: bool) -> bytearray:
    """渲染一张 size x size 的 RGBA 图标。

    simple=True 走小尺寸简化路径（16/24/32）：金环减细、暖光晕收掉，
    否则 16px 下细节会糊成一团，能留的只有「环 + 点」这个剪影。
    """
    ss = 4                       # 4x4 超采样
    S = size * ss
    px = bytearray(S * S * 4)
    inv = 1.0 / S

    orbit_w = ORBIT_W * (0.92 if simple else 1.0)
    star_r = STAR_R * (0.94 if simple else 1.0)
    star_soft = STAR_SOFT * (0.0 if simple else 1.0)   # 16px 不画光晕

    # 逆时针把金环角度转成「像素坐标里的旋转矩阵」：WPF 的 y 轴朝下，
    # 所以顺时针在数学上是负角。
    th = math.radians(-ORBIT_DEG)
    ct, st = math.cos(th), math.sin(th)

    for y in range(S):
        ny = (y + 0.5) * inv
        for x in range(S):
            nx = (x + 0.5) * inv
            i = (y * S + x) * 4

            dx, dy = nx - 0.5, ny - 0.5
            dist = math.hypot(dx, dy)
            if dist > R_OUT + 0.004:
                continue

            # ---- 底：环 + 心 ----
            t = (R_OUT - dist) / RING_W
            if t > 1.0:
                # 圆心：中心略亮制造球感
                k = max(0.0, dist / R_OUT)
                col = _lerp(CORE_HI, CORE, min(1.0, k ** 0.8))
            else:
                # 渐变沿左上->右下这条对角线取，不是沿半径
                col = _grad(max(0.0, min(1.0, (nx + ny) * 0.5)))
                # 环的内外描边，边缘才立得住
                if t < 0.10:
                    col = _lerp(col, (86, 74, 168), 1.0 - t / 0.10)
                elif t > 0.90:
                    col = _lerp(col, (226, 210, 255), (t - 0.90) / 0.10)

            # ---- 中心暖光点 ----
            if dist < star_soft:
                col = _lerp(col, STAR, 1.0 - (dist / star_soft) ** 2)
            if dist < star_r:
                col = _lerp(col, STAR, 1.0)

            # ---- 倾斜金环（椭圆描边）----
            # 判据是「归一化椭圆半径接近 1」：
            #   q = sqrt((x'/rx)^2 + (y'/ry)^2)，q == 1 才是环线所在。
            # 比解析解方程简单，且天然抗锯齿。
            ex = dx * ct - dy * st
            ey = dx * st + dy * ct
            q = math.hypot(ex / ORBIT_RX, ey / ORBIT_RY)
            # 像素到环线的最短距离（近似：沿法向的偏差）
            dd = abs(q - 1.0) * ORBIT_RY * min(1.0, ORBIT_RX / ORBIT_RY) \
                if q > 1e-6 else 1.0
            if dd < orbit_w:
                k = min(1.0, (orbit_w - dd) / (orbit_w * 0.45))
                col = _lerp(col, RING_GOLD, k * 0.92)

            px[i] = col[0]
            px[i + 1] = col[1]
            px[i + 2] = col[2]
            px[i + 3] = 255

    # 盒式降采样求均值
    out = bytearray(size * size * 4)
    n = ss * ss
    for y in range(size):
        for x in range(size):
            r_ = g_ = b_ = a_ = 0
            for sy in range(ss):
                base = ((y * ss + sy) * S + x * ss) * 4
                for sx in range(ss):
                    j = base + sx * 4
                    r_ += px[j]
                    g_ += px[j + 1]
                    b_ += px[j + 2]
                    a_ += px[j + 3]
            o = (y * size + x) * 4
            out[o] = r_ // n
            out[o + 1] = g_ // n
            out[o + 2] = b_ // n
            out[o + 3] = a_ // n
    return out


def _png(size: int, rgba) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        c = tag + data
        return (struct.pack(">I", len(data)) + c
                + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF))

    stride = size * 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)                      # 每行的 filter 字节
        raw += rgba[y * stride:(y + 1) * stride]
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR",
                    struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def _ico(images) -> bytes:
    """打包多尺寸 ICO。

    entries 里的 dwBytesInRes / dwImageOffset 都要按**文件偏移**算，
    不是相对段内偏移——写错了图标仍然能加载但尺寸全错。
    """
    n = len(images)

    def bmp_bytes(size, rgba):
        hdr = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                          size * size * 4, 0, 0, 0, 0)
        body = bytearray()
        stride = size * 4
        for y in range(size - 1, -1, -1):        # BMP 自下而上
            row = rgba[y * stride:(y + 1) * stride]
            for x in range(size):
                o = x * 4
                body += bytes((row[o + 2], row[o + 1], row[o], row[o + 3]))
        rowbytes = ((size + 31) // 32) * 4
        return hdr + bytes(body) + b"\x00" * (rowbytes * size)

    blobs = [bmp_bytes(sz, rgba) for sz, rgba in images]
    off = 6 + 16 * n
    entries = b""
    for (sz, _), blob in zip(images, blobs):
        entries += struct.pack("<BBBBHHII", sz % 256, sz % 256, 0, 0, 1, 32,
                               len(blob), off)
        off += len(blob)
    return struct.pack("<HHH", 0, 1, n) + entries + b"".join(blobs)


def main() -> None:
    cache: dict[tuple[int, bool], bytearray] = {}

    def img(sz: int, simple: bool) -> bytearray:
        key = (sz, simple)
        if key not in cache:
            cache[key] = _render(sz, simple)
        return cache[key]

    made = []
    for sz in SIZES:
        path = OUT_DIR / f"icon_{sz}.png"
        path.write_bytes(_png(sz, img(sz, sz <= 32)))
        made.append((str(path.relative_to(ROOT)), path.stat().st_size))

    ico_sizes = [s for s in SIZES if s in (16, 24, 32, 48, 64, 256)]
    ic = _ico([(s, img(s, s <= 32)) for s in ico_sizes])
    ico_path = OUT_DIR / "AppIcon.ico"
    ico_path.write_bytes(ic)

    # README 用的大图（256，与docs/anim.gif 里的图标一致）
    (DOCS_DIR / "icon256.png").write_bytes(_png(256, img(256, False)))

    for name, size in made:
        print(f"  {name:<44} {size:>8,} bytes")
    print(f"  {str(ico_path.relative_to(ROOT)):<44} {len(ic):>8,} bytes  "
          f"({len(ico_sizes)} sizes)")
    print(f"  {'docs/icon256.png':<44} "
          f"{(DOCS_DIR / 'icon256.png').stat().st_size:>8,} bytes")


if __name__ == "__main__":
    main()
