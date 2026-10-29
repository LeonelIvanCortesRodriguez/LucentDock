import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const capture = process.env.LUCENT_CAPTURE_ROOT;
const build = process.env.LUCENT_BUILD_ROOT;
const ffmpeg = process.env.LUCENT_FFMPEG || "ffmpeg";
if (!capture || !fs.existsSync(capture)) throw new Error("Set LUCENT_CAPTURE_ROOT to the verified staging/capture folder");
if (!build) throw new Error("Set LUCENT_BUILD_ROOT to the private rebuild scratch folder");

const work = path.join(build, "hero");
fs.mkdirSync(work, { recursive: true });
const run = (args) => execFileSync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", ...args], { cwd: work, stdio: "inherit" });
const frame = (name) => path.join(capture, "frames", name);
const transition = path.join(work, "transition.mp4");

run([
  "-framerate", "60", "-start_number", "300", "-i", path.join(capture, "frames", "frame-%04d.png"),
  "-frames:v", "121", "-vf", "crop=2000:170:850:0,scale=1280:360:flags=lanczos,fps=20",
  "-an", "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", transition
]);

const ass = `[Script Info]
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Caption,Segoe UI,38,&H00FFFFFF,&H000000FF,&H00111620,&HC0111620,-1,0,0,0,100,100,0,0,3,8,0,8,40,40,28,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:03.20,Caption,,0,0,0,,10 minutes to a meeting
Dialogue: 0,0:00:03.20,0:00:07.05,Caption,,0,0,0,,Calendar forward, leisure widgets away
`;
fs.writeFileSync(path.join(work, "captions.ass"), ass, "utf8");

const heroMp4 = path.join(work, "hero.mp4");
run([
  "-loop", "1", "-t", "2.4", "-i", frame("frame-0100.png"),
  "-i", transition,
  "-loop", "1", "-t", "2.65", "-i", frame("frame-0820.png"),
  "-filter_complex",
  "[0:v]crop=2000:170:850:0,scale=1280:360:flags=lanczos,fps=20,trim=duration=2.4,setpts=PTS-STARTPTS[b];" +
    "[1:v]fps=20,setpts=PTS-STARTPTS[t];" +
    "[2:v]crop=2000:170:850:0,scale=1280:360:flags=lanczos,fps=20,trim=duration=2.65,setpts=PTS-STARTPTS[a];" +
    "[b][t][a]concat=n=3:v=1:a=0,subtitles=captions.ass[v]",
  "-map", "[v]", "-an", "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", heroMp4
]);

const palette = path.join(work, "palette.png");
run(["-i", heroMp4, "-vf", "fps=15,palettegen=max_colors=160:stats_mode=diff", palette]);
const output = path.join(ROOT, "docs", "media", "hero.gif");
run(["-i", heroMp4, "-i", palette, "-lavfi", "fps=15[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle", "-loop", "0", output]);

const size = fs.statSync(output).size;
if (size >= 5_000_000) throw new Error(`hero.gif is ${(size / 1_000_000).toFixed(2)} MB; expected under 5 MB`);
console.log(`hero.gif ${(size / 1_000_000).toFixed(2)} MB, 1280x360, real frames 0100 → 0300..0420 → 0820`);
