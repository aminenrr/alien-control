# Generates the Classic chime pack (the v1.2 sounds). Since 1.3.0-beta.2 they live in src/AlienControl/Assets/Sounds/Classic/.
import math, wave, struct, random
SR = 48000
def note(f, dur=0.75, amp=0.28):
    n = int(SR * dur); out = []
    for i in range(n):
        t = i / SR
        env = min(1, t / 0.004) * math.exp(-t * 6.0)
        s = math.sin(2*math.pi*f*t) + 0.28*math.sin(2*math.pi*2*f*t)*math.exp(-t*10) + 0.08*math.sin(2*math.pi*3.01*f*t)*math.exp(-t*16)
        rel = n - i                       # 1.2.0-beta.3: smooth release, no cut-off click at the note's end
        if rel < int(SR * 0.08): env *= 0.5 - 0.5 * math.cos(math.pi * rel / (SR * 0.08))
        out.append(amp * env * s)
    return out
def mix(parts, total):
    buf = [0.0] * int(SR * (total + 0.1))
    for off, smp in parts:
        o = int(SR * off)
        for i, v in enumerate(smp):
            if o + i < len(buf): buf[o + i] += v
    return buf
LEAD = 0.04   # 1.2.0-beta.3: 40 ms of silence first so a sleeping audio device wakes up before the note (no pop)
def save(name, buf):
    buf = [0.0] * int(SR * LEAD) + buf
    fo = int(SR * 0.05)                   # and a final fade, in case a tail still rings at the end of the file
    for i in range(fo): buf[-fo + i] *= 0.5 + 0.5 * math.cos(math.pi * i / fo)
    pk = max(abs(x) for x in buf) or 1; g = min(1, 0.5 / pk)
    w = wave.open(name, "wb"); w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(b"".join(struct.pack("<h", int(x * g * 32767)) for x in buf)); w.close()
    return [x * g for x in buf]
# notes (Hz)
G5, A5, C6, D6, E6, G6, B5, E5 = 783.99, 880.0, 1046.5, 1174.66, 1318.51, 1567.98, 987.77, 659.25
S = {
 "quiet":       mix([(0, note(G5))], .8),
 "balanced":    mix([(0, note(C6))], .8),
 "performance": mix([(0, note(E6))], .8),
 "gmode-on":    mix([(0, note(C6, .5, .22)), (.07, note(E6, .5, .22)), (.14, note(G6, .8))], 1.0),
 "gmode-off":   mix([(0, note(G6, .5, .24)), (.11, note(C6))], .95),
 "plug":        mix([(0, note(E5, .6, .26)), (.12, note(B5))], .95),
 "unplug":      mix([(0, note(B5, .6, .22)), (.12, note(E5, .75, .22))], .95),
}
allb = []
for k, b in S.items():
    allb += save(f"snd-{k}.wav", b) + [0.0] * int(SR * .45)
save("ac-chimes-preview.wav", allb)
