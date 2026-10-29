import fs from "node:fs";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const MEDIA = path.join(ROOT, "docs", "media");
const PREVIEWS = path.join(ROOT, "previews");
const SCENES = ["title", "problem", "meeting", "undo", "groups", "styles", "proof", "role", "result"];
const RATE = 48000;

const requiredEnv = (name) => {
  const value = process.env[name];
  if (!value || !fs.existsSync(value)) throw new Error(`Set ${name} to an existing local path`);
  return value;
};
const capture = requiredEnv("LUCENT_CAPTURE_ROOT");
const build = process.env.LUCENT_BUILD_ROOT;
if (!build) throw new Error("Set LUCENT_BUILD_ROOT to the private rebuild scratch folder");
const ffmpeg = requiredEnv("LUCENT_FFMPEG");
const voicePython = requiredEnv("LEDGER_VOICE_PYTHON");
const say = requiredEnv("LEDGER_SAY");
const hyperframes = requiredEnv("LEDGER_HYPERFRAMES");
const gsap = requiredEnv("LEDGER_GSAP");
const font = requiredEnv("LEDGER_STORY_FONT");
const langs = process.argv.slice(2).filter((arg) => ["en", "es"].includes(arg));
if (!langs.length) langs.push("en", "es");

const run = (file, args, options = {}) => execFileSync(file, args, { stdio: "inherit", ...options });
const ff = (args, options = {}) => run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", ...args], options);
const work = path.join(build, "story");
const assets = path.join(work, "shared-assets");
fs.mkdirSync(assets, { recursive: true });

function copy(source, name = path.basename(source)) {
  if (!fs.existsSync(source)) throw new Error(`Missing required asset: ${source}`);
  fs.copyFileSync(source, path.join(assets, name));
}

function prepareAssets() {
  ff(["-i", path.join(capture, "frames", "frame-0100.png"), "-vf", "crop=2000:170:850:0,scale=1760:500:flags=lanczos", "-frames:v", "1", path.join(assets, "before.png")]);
  ff(["-i", path.join(capture, "frames", "frame-0820.png"), "-vf", "crop=2000:170:850:0,scale=1760:500:flags=lanczos", "-frames:v", "1", path.join(assets, "after.png")]);
  for (const name of [
    "command-palette.png", "flyout-calendar-glass.png", "settings-automations.png",
    "dock-glass.png", "settings-taskbar.png", "settings-privacy.png",
    "dock-glass-fullwidth.png", "dock-clear.png", "dock-dark.png"
  ]) copy(path.join(PREVIEWS, name));
  fs.copyFileSync(gsap, path.join(assets, "gsap.min.js"));
  fs.copyFileSync(font, path.join(assets, "story.woff2"));

  ff([
    "-framerate", "60", "-start_number", "300", "-i", path.join(capture, "frames", "frame-%04d.png"),
    "-frames:v", "121", "-vf", "crop=2000:170:850:0,scale=1920:500:flags=lanczos,fps=24",
    "-an", "-c:v", "libx264", "-crf", "15", "-pix_fmt", "yuv420p", path.join(assets, "transition.mp4")
  ]);
  ff([
    "-framerate", "120", "-start_number", "50", "-i", path.join(capture, "frames-undo", "frame-%04d.png"),
    "-frames:v", "1101", "-vf", "crop=2000:170:850:0,scale=1920:500:flags=lanczos,fps=24",
    "-an", "-c:v", "libx264", "-crf", "15", "-pix_fmt", "yuv420p", path.join(assets, "undo.mp4")
  ]);
}

function readWav(file) {
  const buf = fs.readFileSync(file);
  let offset = 12, format = null, data = null;
  while (offset + 8 <= buf.length) {
    const id = buf.toString("ascii", offset, offset + 4);
    const size = buf.readUInt32LE(offset + 4);
    if (id === "fmt ") format = { channels: buf.readUInt16LE(offset + 10), rate: buf.readUInt32LE(offset + 12), bits: buf.readUInt16LE(offset + 22) };
    if (id === "data") data = buf.subarray(offset + 8, offset + 8 + size);
    offset += 8 + size + (size % 2);
  }
  if (!format || !data || format.bits !== 16 || format.channels !== 1) throw new Error(`${file}: expected 16-bit mono PCM`);
  return { rate: format.rate, samples: new Int16Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.length)) };
}

function writeWav(file, samples, rate) {
  const header = Buffer.alloc(44);
  header.write("RIFF", 0); header.writeUInt32LE(36 + samples.length * 2, 4); header.write("WAVE", 8);
  header.write("fmt ", 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(rate, 24); header.writeUInt32LE(rate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34);
  header.write("data", 36); header.writeUInt32LE(samples.length * 2, 40);
  fs.writeFileSync(file, Buffer.concat([header, Buffer.from(samples.buffer, samples.byteOffset, samples.length * 2)]));
}

function loudness(file) {
  const result = spawnSync(ffmpeg, ["-hide_banner", "-nostats", "-i", file, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], { encoding: "utf8" });
  if (result.status !== 0) throw new Error(`Loudness measurement failed: ${result.stderr.slice(-500)}`);
  const json = result.stderr.slice(result.stderr.lastIndexOf("{"), result.stderr.lastIndexOf("}") + 1);
  return JSON.parse(json);
}

function normalizeVoice(raw, output) {
  const measured = loudness(raw);
  ff([
    "-i", raw, "-af",
    `loudnorm=I=-16:TP=-1.5:LRA=11:measured_I=${measured.input_i}:measured_TP=${measured.input_tp}:measured_LRA=${measured.input_lra}:measured_thresh=${measured.input_thresh}:offset=${measured.target_offset}:linear=true`,
    "-ar", String(RATE), "-ac", "1", "-c:a", "pcm_s16le", output
  ]);
}

function stamp(seconds, comma = false) {
  const ms = Math.max(0, Math.round(seconds * 1000));
  const h = Math.floor(ms / 3_600_000), m = Math.floor(ms / 60_000) % 60, s = Math.floor(ms / 1000) % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}${comma ? "," : "."}${String(ms % 1000).padStart(3, "0")}`;
}

function buildTiming(narration, manifest, voiceDir) {
  const recorded = new Map(manifest.lines.map((line) => [line.id, line]));
  const takes = narration.lines.map((line) => {
    const entry = recorded.get(line.id);
    if (!entry || entry.text !== line.text) throw new Error(`${line.id}: recording does not match narration`);
    return { ...line, ...readWav(path.join(voiceDir, entry.file)) };
  });
  const LEAD = .8, GAP_IN = .55, GAP_SCENE = 1.25, TAIL = 3.2;
  let cursor = LEAD;
  const lines = {};
  takes.forEach((take, index) => {
    const seconds = take.samples.length / take.rate;
    lines[take.id] = { scene: take.scene, start: +cursor.toFixed(3), end: +(cursor + seconds).toFixed(3), show: take.show, text: take.text };
    const next = takes[index + 1];
    cursor += seconds + (next ? (next.scene === take.scene ? GAP_IN : GAP_SCENE) : 0);
  });
  const total = +(cursor + TAIL).toFixed(3);
  const scenes = {};
  for (const name of SCENES) {
    const own = Object.values(lines).filter((line) => line.scene === name);
    if (!own.length) throw new Error(`Scene ${name} has no narration`);
    scenes[name] = { start: name === SCENES[0] ? 0 : +(own[0].start - .7).toFixed(3) };
  }
  SCENES.forEach((name, index) => { scenes[name].end = index + 1 < SCENES.length ? scenes[SCENES[index + 1]].start : total; });
  return { takes, lines, total, scenes };
}

function assembleVoice(timing, output) {
  const sourceRate = timing.takes[0].rate;
  if (timing.takes.some((take) => take.rate !== sourceRate)) throw new Error("Narration takes do not share one sample rate");
  const samples = new Int16Array(Math.ceil(timing.total * sourceRate));
  for (const take of timing.takes) {
    samples.set(take.samples, Math.round(timing.lines[take.id].start * sourceRate));
  }
  const raw = `${output}.raw.wav`;
  writeWav(raw, samples, sourceRate);
  normalizeVoice(raw, output);
  fs.rmSync(raw);
}

function writeCaptions(lang, timing) {
  const values = Object.values(timing.lines);
  const srt = values.map((line, index) => {
    const next = values[index + 1];
    const end = Math.max(line.end + .25, Math.min(next?.start ?? timing.total, line.end + .9));
    return `${index + 1}\n${stamp(line.start, true)} --> ${stamp(end, true)}\n${line.text}\n`;
  }).join("\n");
  fs.writeFileSync(path.join(MEDIA, `story-${lang}.srt`), srt, "utf8");
  fs.writeFileSync(path.join(MEDIA, `timing-${lang}.json`), `${JSON.stringify({ lang, total: timing.total, lines: timing.lines, scenes: timing.scenes }, null, 2)}\n`, "utf8");
}

function composition(lang, timing, project) {
  fs.mkdirSync(project, { recursive: true });
  const projectAssets = path.join(project, "assets");
  fs.cpSync(assets, projectAssets, { recursive: true });
  const media = {
    meeting: { start: +(timing.lines.meeting_change.start - .12).toFixed(3), duration: +(121 / 60).toFixed(3) },
    undo: { start: +(timing.scenes.undo.start + .45).toFixed(3), duration: +(1101 / 120).toFixed(3) }
  };
  const story = { lang, total: timing.total, lines: timing.lines, scenes: timing.scenes, media };
  let html = fs.readFileSync(path.join(ROOT, "video", "lucent-story-template.html"), "utf8");
  html = html.replace("/*__STORY__*/null", JSON.stringify(story).replace(/<\//g, "<\\/"))
    .replaceAll("__TOTAL__", String(timing.total))
    .replaceAll("__MEETING_VIDEO_START__", String(media.meeting.start))
    .replaceAll("__MEETING_VIDEO_DURATION__", String(media.meeting.duration))
    .replaceAll("__UNDO_VIDEO_START__", String(media.undo.start))
    .replaceAll("__UNDO_VIDEO_DURATION__", String(media.undo.duration));
  fs.writeFileSync(path.join(project, "index.html"), html, "utf8");
}

function renderLanguage(lang) {
  const narrationPath = path.join(MEDIA, `narration.${lang}.json`);
  const narration = JSON.parse(fs.readFileSync(narrationPath, "utf8"));
  const langWork = path.join(work, lang);
  const voiceDir = path.join(langWork, "takes");
  fs.mkdirSync(langWork, { recursive: true });
  const priorManifest = path.join(voiceDir, "manifest.json");
  const reusable = fs.existsSync(priorManifest) && (() => {
    const prior = JSON.parse(fs.readFileSync(priorManifest, "utf8"));
    return prior.voice === narration.voice && prior.lang === narration.lang && prior.speed === narration.speed &&
      prior.lines.length === narration.lines.length && prior.lines.every((line, index) => line.text === narration.lines[index].text && fs.existsSync(path.join(voiceDir, line.file)));
  })();
  if (!reusable) run(voicePython, ["-I", say, narrationPath, voiceDir], { cwd: path.resolve(path.dirname(say), "..", "..") });
  else console.log(`${lang}: reusing ${narration.lines.length} matching Kokoro takes`);
  const manifest = JSON.parse(fs.readFileSync(path.join(voiceDir, "manifest.json"), "utf8"));
  const timing = buildTiming(narration, manifest, voiceDir);
  const voice = path.join(langWork, `voice-${lang}.wav`);
  assembleVoice(timing, voice);
  writeCaptions(lang, timing);

  const project = path.join(langWork, "hyperframes");
  composition(lang, timing, project);
  const frames = path.join(langWork, "frames.mp4");
  const env = { ...process.env, DO_NOT_TRACK: "1", HYPERFRAMES_NO_TELEMETRY: "1" };
  for (const key of Object.keys(env)) if (/^(AWS_|HEYGEN)/i.test(key)) delete env[key];
  env.Path = `${path.dirname(ffmpeg)}${path.delimiter}${env.Path || ""}`;
  run(process.execPath, [hyperframes, "lint", project], { cwd: project, env });
  run(process.execPath, [hyperframes, "render", "-f", "24", "-q", "standard", "-w", "4", "--experimental-fast-capture=false", "--crf", "18", "--video-frame-format", "png", "-o", frames], { cwd: project, env });

  const output = path.join(MEDIA, `story-${lang}.mp4`);
  ff(["-i", frames, "-i", voice, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "slow", "-crf", "27", "-pix_fmt", "yuv420p", "-r", "24", "-c:a", "aac", "-b:a", "112k", "-ar", "48000", "-t", String(timing.total), "-movflags", "+faststart", output]);
  let size = fs.statSync(output).size;
  if (size >= 10_000_000) {
    const smaller = path.join(langWork, `story-${lang}-smaller.mp4`);
    ff(["-i", output, "-c:v", "libx264", "-preset", "slow", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", smaller]);
    fs.copyFileSync(smaller, output);
    size = fs.statSync(output).size;
  }
  if (size >= 10_000_000) throw new Error(`${path.basename(output)} is ${(size / 1_000_000).toFixed(2)} MB; expected under 10 MB`);
  const measured = loudness(output);
  console.log(`${lang}: ${timing.total.toFixed(1)} s, ${(size / 1_000_000).toFixed(2)} MB, ${Number(measured.input_i).toFixed(1)} LUFS`);
  return { output, timing };
}

prepareAssets();
const results = langs.map(renderLanguage);
if (langs.includes("en")) {
  ff(["-ss", "2.0", "-i", path.join(MEDIA, "story-en.mp4"), "-frames:v", "1", "-compression_level", "7", path.join(MEDIA, "poster.png")]);
}
console.log(`Built ${results.length} story video${results.length === 1 ? "" : "s"} with Kokoro and HyperFrames.`);
