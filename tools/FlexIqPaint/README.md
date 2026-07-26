# flex-iq-paint

Turns a PNG into a signal that draws it on a waterfall. Image **columns become frequencies**, image
**rows become time**.

```sh
# Mono audio for the DAX path
flex-iq-paint logo.png --out logo.f32 --rate 48000 --lo 300 --hi 2700
flex-dax-tx --radio <ip> --freq 14.100 --rate 48000 --in logo.f32

# Complex IQ for the waveform path, which has room for a lot more detail
flex-iq-paint logo.png --out logo.cf32 --iq --rate 24000 --lo 300 --hi 9500 --bins 384
flex-iq-tx --radio <ip> --freq 14.1905 --bw 9500 --reference loweredge --in logo.cf32
```

The picture is **ordinary complex baseband** at the frequencies you ask for, running upward from
`--lo`. Which half of the spectrum a Flex waveform actually transmits is the library's problem, not
the picture's — `flex-iq-tx` derives the slice, shifts the samples and sets the filter.

Note there is **no `--direct`** above. That flag replays a stream verbatim, which is right for a capture
or for the corpus probes that deliberately test the radio's sideband behaviour, but wrong here: a
picture pre-placed below DC bakes a quirk of one radio into the file and ties it to one transmit
path. This tool used to do exactly that, and it leaked the concept straight back out to the caller.

## Three things that decide whether it is legible

All three are the failure modes the rest of this kit exists to catch, drawn where you can see them:

- **The oscillators run continuously**, one per frequency bin, amplitude-modulated by the pixels
  above them. Restarting a tone each row is a phase discontinuity, which is a click, which is
  broadband — a bright line straight across the picture.
- **Peak-limited, not RMS-normalised.** Hundreds of tones at random phase have a crest factor near
  20 dB. Normalising the RMS to something sensible puts the peaks well past full scale, and one
  clipped sample splatters every bin at once.
- **Random initial phases.** With every tone starting at zero their peaks coincide and the crest
  factor is the *bin count*.

## Resolution

`--line-ms` must be at least `1000 / spacing`, where spacing is `(hi − lo) / (bins − 1)`. Below that
the waterfall cannot resolve adjacent bins and the picture smears — the tool warns rather than
letting it look like the radio blurring it.

The IQ path fits about four times the detail of a 3 kHz audio one, because it has the full ~10 kHz
to work with rather than sharing it with an SSB filter's skirts.

## PNG support

8-bit greyscale, RGB, RGBA and grey+alpha, plus 1/2/4/8-bit palette, non-interlaced. The decoder is
hand-rolled against `ZLibStream` rather than taking a package: the only thing needed is a brightness
grid, and the alternatives are a licence question in an AGPL repo or a platform one on Linux.
