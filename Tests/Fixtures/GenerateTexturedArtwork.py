"""Generate an original deterministic PNG fixture; no downloaded artwork is used."""
from pathlib import Path
import struct
import zlib

width, height = 1280, 640
raw = bytearray()
for y in range(height):
    raw.append(0)
    for x in range(width):
        noise = ((x * 73856093) ^ (y * 19349663)) % 32
        if 365 <= x < 908 and 80 <= y < 614:
            raw.extend((115 + noise, 140 + noise, 105 + noise))
        else:
            raw.extend((18 + noise, 16 + noise, 20 + noise))

def chunk(kind, payload):
    return struct.pack('!I', len(payload)) + kind + payload + struct.pack('!I', zlib.crc32(kind + payload))

png = b'\x89PNG\r\n\x1a\n'
png += chunk(b'IHDR', struct.pack('!IIBBBBB', width, height, 8, 2, 0, 0, 0))
png += chunk(b'IDAT', zlib.compress(raw, 9))
png += chunk(b'IEND', b'')
Path(__file__).with_name('dark-textured-artwork.png').write_bytes(png)
