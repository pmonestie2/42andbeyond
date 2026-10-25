"""Generate a repeatable, seamless deep-space panorama for the Unity skybox."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image


def value_noise(direction: np.ndarray, frequency: int, seed: int) -> np.ndarray:
    """Sample smooth three-dimensional noise on unit directions.

    Returns:
        np.ndarray: Seamless values in the range zero to one.
    """
    random = np.random.default_rng(seed)
    size = frequency * 2 + 5
    lattice = random.random((size, size, size), dtype=np.float32)
    coordinates = direction * frequency + frequency + 2
    cell = np.floor(coordinates).astype(np.int32)
    blend = coordinates - cell
    blend *= blend * (3 - 2 * blend)
    result = np.zeros(direction.shape[:2], dtype=np.float32)
    for dz in (0, 1):
        wz = blend[..., 2] if dz else 1 - blend[..., 2]
        for dy in (0, 1):
            wy = blend[..., 1] if dy else 1 - blend[..., 1]
            for dx in (0, 1):
                wx = blend[..., 0] if dx else 1 - blend[..., 0]
                result += lattice[cell[..., 0] + dx, cell[..., 1] + dy,
                                  cell[..., 2] + dz] * wx * wy * wz
    return result


def render(width: int, height: int, seed: int) -> Image.Image:
    """Build a blue-violet galactic band with dark lanes and sparse bright stars.

    Returns:
        Image.Image: RGB equirectangular sky panorama.
    """
    longitude = ((np.arange(width, dtype=np.float32) + .5) / width *
                 (2 * np.pi) - np.pi)[None, :]
    latitude = ((np.arange(height, dtype=np.float32) + .5) / height *
                np.pi - np.pi / 2)[:, None]
    cos_latitude = np.cos(latitude)
    direction = np.empty((height, width, 3), dtype=np.float32)
    direction[..., 0] = cos_latitude * np.cos(longitude)
    direction[..., 1] = np.sin(latitude)
    direction[..., 2] = cos_latitude * np.sin(longitude)
    # The inclined disk wraps naturally because it is defined in 3D direction space.
    disk_normal = np.array([.17, .9, -.39], dtype=np.float32)
    disk_normal /= np.linalg.norm(disk_normal)
    disk_distance = np.abs(direction[..., 0] * disk_normal[0] +
                           direction[..., 1] * disk_normal[1] +
                           direction[..., 2] * disk_normal[2])
    broad = np.exp(-np.square(disk_distance / .33))
    narrow = np.exp(-np.square(disk_distance / .135))
    coarse = value_noise(direction, 3, seed)
    medium = value_noise(direction, 8, seed + 1)
    fine = value_noise(direction, 22, seed + 2)
    detail = .5 * coarse + .34 * medium + .16 * fine
    nebula = broad * np.clip((detail - .34) * 2.8, 0, 1)
    dust = narrow * np.clip((value_noise(direction, 11, seed + 3) - .42) * 2.2, 0, 1)
    # A broad violet cloud complements the black hole's amber disk.
    blue_cloud = np.exp(-np.square((direction[..., 0] - .32) / .65) -
                        np.square((direction[..., 1] + .08) / .35))
    amber_cloud = np.exp(-np.square((direction[..., 0] + .52) / .48) -
                         np.square((direction[..., 2] - .34) / .42))
    image = np.zeros((height, width, 3), dtype=np.float32)
    image[:] = (.006, .012, .028)
    image += (broad * (.012 + .032 * detail))[..., None] * (.43, .64, 1.0)
    image += (nebula * blue_cloud * .2)[..., None] * (.3, .65, 1.0)
    image += (nebula * (1 - blue_cloud) * .11)[..., None] * (.52, .35, 1.0)
    image += (nebula * amber_cloud * .095)[..., None] * (1.0, .42, .18)
    image *= (1 - dust * .72)[..., None]
    rng = np.random.default_rng(seed + 4)
    for index in range(width * height // 290):
        x = int(rng.integers(width))
        y = int(rng.integers(height))
        concentration = .35 + .65 * float(broad[y, x])
        if rng.random() > concentration:
            continue
        intensity = float(rng.power(7) * .57 + .09)
        tint = np.array((.66, .8, 1.0) if index % 5 else (1.0, .79, .57),
                        dtype=np.float32)
        image[y, x] += tint * intensity
        if intensity > .48:
            for ox, oy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                image[(y + oy) % height, (x + ox) % width] += tint * intensity * .12
    image = np.clip(image, 0, 1)
    return Image.fromarray((image * 255).astype(np.uint8))


def main() -> None:
    """Write the bundled panorama using an explicit seed and resolution."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=42042)
    parser.add_argument('--width', type=int, default=2048)
    parser.add_argument('--height', type=int, default=1024)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] /
                        'Assets/FortyTwoAndBeyond/Resources/ProceduralSpace.png')
    args = parser.parse_args()
    if args.width < 512 or args.height < 256 or args.width != args.height * 2:
        parser.error('Use a 2:1 panorama of at least 512 × 256 pixels.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    render(args.width, args.height, args.seed).save(args.output, optimize=True)
    print(args.output)


if __name__ == '__main__':
    main()
