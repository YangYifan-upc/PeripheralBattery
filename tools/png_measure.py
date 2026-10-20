import sys, zlib, struct

def load_png(path):
    data = open(path, 'rb').read()
    pos, idat, w, h = 8, b'', 0, 0
    while pos < len(data):
        length = struct.unpack('>I', data[pos:pos+4])[0]
        ctype = data[pos+4:pos+8]
        chunk = data[pos+8:pos+8+length]
        if ctype == b'IHDR':
            w, h, bit, color = struct.unpack('>IIBB', chunk[:10])
            if bit != 8 or color not in (2, 6):
                raise SystemExit(f'不支持的格式 bit={bit} color={color}')
            bpp = 4 if color == 6 else 3
        elif ctype == b'IDAT':
            idat += chunk
        elif ctype == b'IEND':
            break
        pos += 12 + length

    raw = zlib.decompress(idat)
    stride = w * bpp
    out = bytearray(stride * h)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i-bpp]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0
                c = prev[i-bpp] if i >= bpp else 0
                b = prev[i]
                pa, pb, pc = abs(b-c), abs(a-c), abs(a+b-2*c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        out[y*stride:(y+1)*stride] = line
        prev = line
    return w, h, bpp, out

w, h, bpp, px = load_png(sys.argv[1])
print(f'图像 {w}x{h} 每像素 {bpp} 字节')

# 以左上角像素作为背景色
bg = tuple(px[0:bpp][:3])
print('背景色', bg)

ring_min_x, ring_min_y, ring_max_x, ring_max_y = 10**6, 10**6, -1, -1
dig_min_x, dig_min_y, dig_max_x, dig_max_y = 10**6, 10**6, -1, -1

for y in range(h):
    for x in range(w):
        i = (y*w + x) * bpp
        r, g, b = px[i], px[i+1], px[i+2]
        a = px[i+3] if bpp == 4 else 255
        if a < 40:
            continue
        if abs(r-bg[0]) + abs(g-bg[1]) + abs(b-bg[2]) < 12:
            continue
        # 非背景像素属于图标
        ring_min_x, ring_min_y = min(ring_min_x, x), min(ring_min_y, y)
        ring_max_x, ring_max_y = max(ring_max_x, x), max(ring_max_y, y)
        if r < 120 and g < 120 and b < 130:
            dig_min_x, dig_min_y = min(dig_min_x, x), min(dig_min_y, y)
            dig_max_x, dig_max_y = max(dig_max_x, x), max(dig_max_y, y)

if ring_max_x < 0:
    raise SystemExit('没找到图标像素')
if dig_max_x < 0:
    raise SystemExit('没找到深色数字像素')

rcx, rcy = (ring_min_x + ring_max_x + 1) / 2, (ring_min_y + ring_max_y + 1) / 2
dcx, dcy = (dig_min_x + dig_max_x + 1) / 2, (dig_min_y + dig_max_y + 1) / 2
print(f'圆环 bbox x{ring_min_x}..{ring_max_x} y{ring_min_y}..{ring_max_y}  中心({rcx:.1f},{rcy:.1f}) '
      f'尺寸 {ring_max_x-ring_min_x+1}x{ring_max_y-ring_min_y+1}')
print(f'数字 bbox x{dig_min_x}..{dig_max_x} y{dig_min_y}..{dig_max_y}  中心({dcx:.1f},{dcy:.1f}) '
      f'尺寸 {dig_max_x-dig_min_x+1}x{dig_max_y-dig_min_y+1}')
print(f'数字相对圆环中心偏移 dx={dcx-rcx:+.1f} dy={dcy-rcy:+.1f} 像素')
