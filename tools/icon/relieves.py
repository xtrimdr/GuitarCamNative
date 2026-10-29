"""Engraving maps for the icon's instruments (tools/make-icon.ps1 runs it).

Each instrument drawing (tools/icon/instrumentos, rasterized by Inkscape) becomes a shallow engraving like the ones
on a camera's metal: the filled silhouette is cut into the blade and darkened, and the drawing's own lines stay as
fine bright detail inside it (strings, keys, f-holes). The keyboard has no drawing: its keys are drawn here.
Output: 16-bit PNGs, red = the cut (0..1, with a soft edge), green = the detail lines.

    python relieves.py <rasters dir> <output dir>
"""
import os
import sys

import cv2
import numpy as np

# Soft edge of the cut, in pixels of the 1400 px tall rasters
EDGE = 5
# Empty border kept around each map, so the cut never touches the image edge
MARGIN = 40
# A drawing with more than this share of its silhouette farther than EMPTY pixels from any detail line gets more
# detail (see from_drawing)
SPARSE = 0.25
EMPTY = 45


def soft(mask, r):
    d = cv2.distanceTransform(mask, cv2.DIST_L2, 5)
    return np.clip(d / r, 0, 1)


def from_drawing(path):
    im = cv2.imread(path, cv2.IMREAD_UNCHANGED).astype(np.float32) / 255
    a = im[..., 3]
    lum = im[..., :3].mean(axis=2) * a + (1 - a)
    ink = (1 - lum) * a
    # The silhouette: everything drawn, small gaps closed, holes filled
    solid = (a > 0.1).astype(np.uint8) * 255
    solid = cv2.morphologyEx(solid, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (11, 11)))
    contours, _ = cv2.findContours(solid, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    sil = np.zeros_like(solid)
    cv2.drawContours(sil, contours, -1, 255, cv2.FILLED)
    cut = soft(sil, EDGE)
    # Inner lines only: the outline itself is already the cut's edge. Drawings made of filled shapes (the guitar)
    # have few lines, so the edges between their shapes count as lines too, or the engraving is a black blot.
    inner = cv2.erode(sil, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (13, 13)))
    grey = (lum * 255).astype(np.uint8)
    dot = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    edges = cv2.dilate(cv2.Canny(grey, 40, 110), dot).astype(np.float32) / 255
    lines = cv2.GaussianBlur(np.maximum(ink, edges * 0.8), (0, 0), 1.0) * (inner > 0)
    far = cv2.distanceTransform(((lines < 0.2) & (sil > 0)).astype(np.uint8) * 255, cv2.DIST_L2, 5)
    if (far[sil > 0] > EMPTY).mean() > SPARSE:
        # Large areas without detail (a solid body): finer edges, and a line engraved just inside the outline, so
        # it doesn't read as a blot next to the others
        edges = cv2.dilate(cv2.Canny(grey, 8, 30), dot).astype(np.float32) / 255
        rim = np.zeros_like(sil)
        rc, _ = cv2.findContours(cv2.erode(sil, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (31, 31))),
                                 cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        cv2.drawContours(rim, rc, -1, 255, 5)
        detail = np.maximum(np.maximum(ink, edges), rim.astype(np.float32) / 255)
        lines = cv2.GaussianBlur(detail, (0, 0), 1.0) * (inner > 0)
    return cut, np.clip(lines * 1.4, 0, 1)


def keyboard():
    """Two octaves seen from above, the black keys on top: the gaps and the black keys are the cut."""
    whites, kw, hgt = 14, 200, 760
    w = whites * kw
    cut = np.zeros((hgt, w), np.uint8)
    cv2.rectangle(cut, (0, 0), (w - 1, hgt - 1), 255, 14)
    for j in range(1, whites):
        cv2.line(cut, (j * kw, 0), (j * kw, hgt), 255, 10)
    for j in range(1, whites):
        if j % 7 in (1, 2, 4, 5, 6):
            cv2.rectangle(cut, (int(j * kw - kw * 0.3), 0), (int(j * kw + kw * 0.3), int(hgt * 0.62)), 255, cv2.FILLED)
    return soft(cut, 3), np.zeros((hgt, w), np.float32)


def save(cut, lines, path):
    rgb = np.dstack([np.zeros_like(cut), lines, cut])  # OpenCV writes BGR
    rgb = np.pad(rgb, ((MARGIN, MARGIN), (MARGIN, MARGIN), (0, 0)))
    cv2.imwrite(path, (np.clip(rgb, 0, 1) * 65535).astype(np.uint16))


def main():
    src, dst = sys.argv[1], sys.argv[2]
    os.makedirs(dst, exist_ok=True)
    for name in ("guitarra", "violin", "microfono", "trompeta", "saxo"):
        save(*from_drawing(os.path.join(src, name + ".png")), os.path.join(dst, name + ".png"))
    save(*keyboard(), os.path.join(dst, "teclado.png"))
    print("OK", dst)


if __name__ == "__main__":
    main()
