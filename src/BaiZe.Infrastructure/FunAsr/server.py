#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""白泽 BaiZe 本地语音识别服务（FunASR）。

WebSocket 协议（与 funasr-runtime 对齐的子集）：
1. 客户端连接后先发一条 JSON 会话配置（本服务读取 is_speaking 字段）
2. 客户端持续发送二进制 PCM 音频（16kHz / 单声道 / s16le）
3. 服务端做能量 VAD 切句，整句送 FunASR 识别，回 JSON：
   {"mode": "offline", "text": "...", "is_final": true}
4. 客户端发 {"is_speaking": false} 时立即冲刷缓冲区内剩余音频

用法：python server.py --port 10095 --device auto [--spk cam++]
"""
import argparse
import asyncio
import glob
import json
import logging
import os
import shutil
import sys
import time
import wave

import numpy as np
import websockets

# 抑制外部 TCP 探活（裸端口扫描/健康检查）的握手报错刷屏（不影响功能）
logging.getLogger("websockets").setLevel(logging.CRITICAL)


def log(msg: str):
    """stdout（cmd 窗口）+ 落盘 setup 日志目录，便于诊断麦克风链路。"""
    print(msg, flush=True)
    try:
        d = os.path.join(os.environ.get("LOCALAPPDATA", "."), "BaiZe", "funasr", "logs")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "funasr-server.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("[%H:%M:%S] ") + msg + "\n")
    except Exception:
        pass


def log_debug(msg: str):
    """仅落盘不刷 stdout：用于启动探活/握手噪声，避免刷屏，诊断信息仍可在日志文件查看。"""
    try:
        d = os.path.join(os.environ.get("LOCALAPPDATA", "."), "BaiZe", "funasr", "logs")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "funasr-server.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("[%H:%M:%S] ") + msg + "\n")
    except Exception:
        pass

def align_cudnn():
    """让 torch 和 onnxruntime 共用同一套 cuDNN（否则重叠语音分离会静默劣化到 CPU）。

    坑的来龙去脉（RTX 3070 Laptop / torch 2.6.0+cu124 / onnxruntime-gpu 1.23 实测）：
    torch 自带 cuDNN 9.1，pip 装来的 nvidia-cudnn-cu12 是 9.26，两边 DLL 都叫 cudnn64_9.dll。
    Windows 加载器按「模块基名」解析隐式依赖，一个进程里只能存在一套，谁先加载谁占坑：
      · torch 先 import → ORT 复用 9.1 → cuDNN Frontend 不支持 MossFormer2 里
        dilation=[2,1] 的 FSMN 卷积图（日志一行 "No execution plans support the graph"）
        → ORT 把整图**静默**回落 CPU，分离 RTF 从 0.59 劣化到 5.65，
          一句 9 秒的音频定稿要 29 秒，客户端等不到定稿就断开；
      · ORT 先加载 → torch 的 cudnn_cnn64_9.dll(9.1) 绑不上 9.26 的导出表，
        import torch 直接 WinError 127，整个 ASR 起不来。
    用 ctypes.CDLL 按绝对路径预加载也无效：显式 LoadLibrary(路径) 不去重，只有隐式依赖才按基名命中。
    唯一解是让两边字面共用同一份 DLL —— 把 pip 那套 9.26 覆盖到 torch/lib
    （cuDNN 9.x 主版本内 ABI 向后兼容，实测 conv1d(dilation=2) / conv2d / fp16 均正常）。
    """
    if os.path.exists(os.path.join(os.path.dirname(__file__), "_cudnn_no_align")):
        return
    sp = None
    try:
        import site
        # ⚠️ Windows venv 里 site.getsitepackages()[0] 是 venv 根目录而不是 site-packages，
        #    所以这里必须逐个候选目录去验，不能只取第一个。
        for p in site.getsitepackages():
            if os.path.isdir(os.path.join(p, "torch")):
                sp = p
                break
    except Exception:
        pass
    if sp is None:
        for p in glob.glob(os.path.join(sys.prefix, "**", "site-packages"), recursive=True):
            if os.path.isdir(os.path.join(p, "torch")):
                sp = p
                break
    if sp is None:
        return

    src = os.path.join(sp, "nvidia", "cudnn", "bin")
    dst = os.path.join(sp, "torch", "lib")
    a, b = os.path.join(src, "cudnn64_9.dll"), os.path.join(dst, "cudnn64_9.dll")
    if not (os.path.isdir(src) and os.path.isdir(dst)) or not (os.path.exists(a) and os.path.exists(b)):
        return
    if os.path.getsize(a) == os.path.getsize(b):
        return  # 已经一致，无需处理（幂等）

    bak = os.path.join(dst, "_cudnn_orig")
    try:
        if not os.path.isdir(bak):
            os.makedirs(bak, exist_ok=True)
            for f in glob.glob(os.path.join(dst, "cudnn*.dll")):
                shutil.copy2(f, bak)
        n = 0
        for f in glob.glob(os.path.join(src, "cudnn*.dll")):
            shutil.copy2(f, dst)
            n += 1
        log(f"[baize-asr] 已对齐 cuDNN：nvidia 9.26 → torch/lib（{n} 个 DLL，"
            f"torch 原版备份在 _cudnn_orig）")
    except Exception as e:
        log(f"[baize-asr] cuDNN 对齐失败（不影响识别，但重叠语音分离可能回落 CPU）: {e}")


ap = argparse.ArgumentParser(description="BaiZe local ASR server (FunASR)")
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=10095)
ap.add_argument("--device", default="auto", help="auto / cuda / cpu")
ap.add_argument("--spk", default=None, help="说话人模型名（如 cam++），留空则关闭说话人分离")
ap.add_argument("--no-stream", dest="stream", action="store_false",
                help="禁用 online 流式中间结果（边说边出字），只做整句定稿")
ap.add_argument("--sep", default=None,
                help="重叠语音分离模型的 ONNX 文件路径（留空则不支持两人同时说话）")
args = ap.parse_args()

# ⚠️ 必须赶在下面 import torch 之前：一旦 torch 把它自带的 cuDNN 9.1 加载进进程，
#    后面 onnxruntime 就只能复用它，再也换不回来了（详见 align_cudnn 的说明）。
align_cudnn()

if args.device == "auto":
    try:
        import torch
        args.device = "cuda" if torch.cuda.is_available() else "cpu"
    except Exception:
        args.device = "cpu"

log(f"[baize-asr] 使用设备: {args.device}")
log("[baize-asr] 正在加载 FunASR 模型（首次运行会自动下载，请耐心等待）...")

from funasr import AutoModel

model_kwargs = dict(
    model="paraformer-zh",
    vad_model="fsmn-vad",
    punc_model="ct-punc",
    device=args.device,
    disable_update=True,
)
if args.spk:
    model_kwargs["spk_model"] = args.spk
model = AutoModel(**model_kwargs)
log("[baize-asr] 离线（定稿）模型加载完成")

# online 流式模型：负责"边说边出字"的局部结果（2pass 的 online 分支）
stream_model = None
if args.stream:
    try:
        stream_model = AutoModel(model="paraformer-zh-streaming",
                                 device=args.device, disable_update=True)
        log("[baize-asr] 在线流式模型加载完成 → 边说边出字")
    except Exception as e:
        log(f"[baize-asr] 流式模型加载失败，回落纯整句模式（无中间结果）: {e}")
else:
    log("[baize-asr] 已禁用流式模式")

# 单独加载 cam++ 向量模型：用于每句抽取说话人 embedding，自建跨句稳定聚类。
# 说明：FunASR 自带 spk 聚类只在"单次 generate 调用内"有效（且 <20 条 embedding 直接全归 0），
# 而本服务每句单独识别，若直接用其 spk 字段则永远只有"说话人 1"。故这里独立抽向量自行聚类。
spk_emb_model = None
if args.spk:
    try:
        spk_emb_model = AutoModel(model=args.spk, device=args.device, disable_update=True)
        log("[baize-asr] 说话人向量模型加载完成 → 支持跨句说话人分离")
    except Exception as e:
        log(f"[baize-asr] 说话人向量模型加载失败，回落无说话人分离: {e}")

# ---- 重叠语音分离（两人同时说话 / 抢话）----
# FunASR 的"说话人分离"只是 diarization（谁在什么时间说话），并没有把叠加混音拆开的能力。
# 要支持同时说话必须额外做源分离。这里用 MossFormer2 的 ONNX 量化版：
#   inputs [N, T] float32 → spk0 / spk1 [N, T]，直接吐两路波形。
# ⚠️ 只在 CUDA 下启用：实测 RTF 在 RTX 3070 Laptop 上是 0.52（够用），纯 CPU 是 3.34
# （一句 6 秒要跑 20 秒），会让定稿延迟爆炸，所以 CPU-only 时直接不加载。
SEP_MAX_RTF = 1.5           # 分离 RTF 超过这个值就判定没跑在 GPU 上，直接弃用分离


def sep_actually_fast(sess) -> bool:
    """实跑一次，确认分离真的在 GPU 上。

    光看 get_providers() 不够：cuDNN 版本不对时 ORT 会在**运行时**把节点逐个回落 CPU，
    provider 列表依旧写着 CUDAExecutionProvider，但速度已经慢了 10 倍。
    所以开机时用 1 秒音频实测：跑两遍取第二遍（第一遍含 CUDA/cuDNN 建图开销），
    RTF 超标就把分离整个关掉 —— 宁可不支持同时说话，也不能让定稿卡死。
    """
    try:
        x = (np.random.default_rng(0).standard_normal(16000, dtype=np.float32) * 0.05)
        sess.run(None, {"inputs": x[None, :]})          # 预热，不计入
        t0 = time.perf_counter()
        sess.run(None, {"inputs": x[None, :]})
        rtf = (time.perf_counter() - t0) / 1.0
        if rtf > SEP_MAX_RTF:
            log(f"[baize-asr] 分离实测 RTF={rtf:.2f}（阈值 {SEP_MAX_RTF}）→ "
                f"判定未跑在 GPU 上（多半是 cuDNN 版本不对被回落 CPU），"
                f"为避免定稿卡死已停用重叠语音分离")
            return False
        log(f"[baize-asr] 分离实测 RTF={rtf:.2f} → GPU 正常")
        return True
    except Exception as e:
        log(f"[baize-asr] 分离速度探测失败，停用: {e}")
        return False


sep_session = None
if args.sep and args.device == "cuda":
    try:
        import onnxruntime as ort
        sep_session = ort.InferenceSession(
            args.sep, providers=["CUDAExecutionProvider", "CPUExecutionProvider"])
        if not sep_actually_fast(sep_session):
            sep_session = None
        else:
            log(f"[baize-asr] 重叠语音分离模型加载完成（{sep_session.get_providers()[0]}）"
                f"→ 支持两人同时说话")
    except Exception as e:
        log(f"[baize-asr] 分离模型加载失败，回落不支持同时说话: {e}")
        sep_session = None
elif args.sep:
    log("[baize-asr] 检测到纯 CPU 环境，不启用重叠语音分离（RTF≈3.3，定稿会严重卡顿）")

log("[baize-asr] 模型加载完成，等待连接")

# ---- 切句参数（能量 VAD）----
SILENCE_MS = 800          # 句尾静音多久判定一句话结束（普通状态）
SILENCE_MS_LONG = 320     # 长句时的静音门槛：说话久了，换气/微停顿也判为结束，避免憋到太久才出字
LONG_UTTERANCE_SEC = 6.0  # 连续说话超过 6 秒进入"长句模式"，用更短的静音门槛
MIN_UTTERANCE_BYTES = 8000   # 少于 ~250ms 的音频不识别
MAX_UTTERANCE_BYTES = 480000  # 硬上限 ~15s：防止说话不停导致缓冲区无限增长、一次性识别过久
RMS_THRESHOLD = 0.01      # 语音能量阈值

# ---- 在线流式（2pass 的 online 分支）----
STREAM_CHUNK_MS = 600                                    # 流式模型每次喂入的音频长度
STREAM_CHUNK_BYTES = 16000 * 2 * STREAM_CHUNK_MS // 1000  # 19200 字节 = 600ms @16k/16bit/mono
SILENCE_KEEP_BYTES = 32000                                # 未说话时缓冲区最多保留 ~1s（避免静音堆积）

# ---- 自动增益（AGC）：解决"麦克风音量太小、必须喊很大声才识别"问题 ----
# 麦Voice 电平偏低的设备，原始 RMS 往往只有 0.003~0.008，贴着固定阈值 0.01，
# 导致只有喊叫才触发 VAD。AGC 把"超过噪声门限的语音"平滑放大到目标 RMS，
# 静音段（低于噪声门限）不放大，避免把底噪当语音。float32 域处理，未到量化底噪，对识别质量无害。
AGC_NOISE_GATE = 0.0015   # 低于此 RMS 视为静音/底噪，不放大（约 -56dB）
AGC_TARGET = 0.15         # 放大目标 RMS（正常说话电平，远超 VAD 阈值 0.01）
AGC_MAX_GAIN = 30.0       # 单块最大增益，防止极端底噪爆音

# ---- 说话人跨句稳定聚类（自建，替代 FunASR 单次调用内聚类）----
# 双阈值 + 短句保护：
#   >= SPK_SIM_SAME              → 判同人，更新质心
#   [SPK_SIM_MERGE, SPK_SIM_SAME) → 灰区，宁并勿拆归入最像者，但不污染质心
#   < SPK_SIM_MERGE 且句子 >=2s   → 新建说话人
# 短句(<2s)声纹噪声大，永不新建说话人（防止同人被拆成多个）。
#
# ⚠️ 阈值依据实测数据设定（scratch/test_spk_robust.py，用 cam++ 自带中文样本 + 加噪/混响模拟）：
#   | 条件                | 同人最低 | 异人最高 | 安全阈值区间   |
#   | 干净实验室          | 0.479   | 0.031   | (0.03, 0.48)  |
#   | 20dB 轻噪           | 0.391   | 0.046   | (0.05, 0.39)  |
#   | 10dB+混响(近真实)   | 0.330   | 0.289   | (0.29, 0.33)  |
#   | 5dB+重混响          | 0.122   | 0.158   | 已重叠，靠阈值无解 |
# 旧值 0.55/0.42 是基于"同人余弦 >0.6"这一错误假设定的，在真实房间（远场+混响）
# 远高于实际的同人相似度(0.33)，导致同一个人被反复判成新人 → 出现"两人识别出三人"。
# 现值 0.34/0.28 落在实测安全区间内。若你的会议室噪声/混响与上述不同，可据此上下微调：
#   嫌把一人拆成多人 → 调低 SPK_SIM_SAME；嫌不同人被合成一人 → 调高 SPK_SIM_MERGE。
# 核心价值观在 SPK_SIM_MERGE：**低于它才允许新建说话人**。它必须落在
#   (异人最高相似度, 同人最低相似度) = (0.289, 0.330)  这条窄缝内：
#   高于 0.330 → 同人中被噪声压低的句子会被误判成新人（"两人识别出三人"）；
#   低于 0.289 → 音色相近的不同人不会被分开（"有时被认成同一个人"）。
# 故取该区间中点 0.31，SIM_SAME 取 0.40（其上是明确同人、更新质心）。
SPK_SIM_SAME = 0.40
SPK_SIM_MERGE = 0.31
SPK_MIN_NEW_SEC = 2.0      # 只有足够长的句子才有资格新建说话人质心

SEP_MIN_SEC = 1.0          # 短于此的音频不再尝试分离（收益极低，纯浪费一次推理）

# ---- 诊断落盘：混音识别为空、但分离两路有文本的音频存成 wav，供离线分析 ----
CAPTURE_DIR = os.path.join(os.environ.get("LOCALAPPDATA", "."),
                           "BaiZe", "funasr", "logs", "capture")
_capture_count = 0


def capture_pcm(pcm: bytes, tag: str):
    """把可疑段存成 16k/16bit/mono wav（每进程最多 5 段，防止撑爆磁盘）。"""
    global _capture_count
    if _capture_count >= 5:
        return
    try:
        _capture_count += 1
        os.makedirs(CAPTURE_DIR, exist_ok=True)
        p = os.path.join(
            CAPTURE_DIR, f"{time.strftime('%H%M%S')}_{tag}_{len(pcm) // 32000}s.wav")
        with wave.open(p, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(16000)
            w.writeframes(pcm)
        log(f"[诊断] 已保存可疑音频样本 → {p}")
    except Exception:
        pass


def apply_agc(chunk: bytes, prev_gain: float):
    """对一块 PCM 做自动增益：语音放大到目标电平，静音不放大。

    返回 (处理后_bytes, 平滑后的增益)。静音段沿用上一次增益，不更新，
    避免静音被放大成伪语音；语音段用指数平滑跨块跟随，防止音量忽大忽小。"""
    n = len(chunk) // 2 * 2
    if n == 0:
        return chunk, prev_gain
    s = np.frombuffer(chunk[:n], dtype=np.int16).astype(np.float32) / 32768.0
    rms = float(np.sqrt(np.mean(s * s)))
    if rms < AGC_NOISE_GATE:
        return chunk, prev_gain            # 静音/底噪不放大
    gain = min(AGC_TARGET / rms, AGC_MAX_GAIN)
    if gain <= 1.0:
        return chunk, prev_gain            # 本来就很响，无需放大
    gain = 0.85 * prev_gain + 0.15 * gain  # 跨块平滑
    s = np.clip(s * gain, -1.0, 1.0)
    out = (s * 32767.0).astype(np.int16).tobytes()
    return out, gain


def recognize(pcm: bytes) -> list[dict]:
    """整句 PCM → [{text, spk}]（阻塞调用，放线程池跑）。
    启用说话人分离（cam++）时按 sentence_info 逐句标注 spk 并合并连续同人；
    未启用或模型未回传说话人信息时 spk = -1。"""
    samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    if samples.size == 0:
        return []
    try:
        res = model.generate(input=samples, fs=16000, cache={},
                             language="auto", use_itn=True, batch_size_s=60,
                             sentence_timestamp=True)
        if res and len(res) > 0:
            infos = res[0].get("sentence_info")
            if infos:
                out: list[dict] = []
                for s in infos:
                    t = (s.get("text") or "").strip()
                    if not t:
                        continue
                    spk = s.get("spk")
                    item = {"text": t, "spk": int(spk) if spk is not None else -1}
                    # 连续同一说话人的句子合并为一段
                    if out and out[-1]["spk"] == item["spk"]:
                        out[-1]["text"] += item["text"]
                    else:
                        out.append(item)
                if out:
                    return out
            text = (res[0].get("text") or "").strip()
            return [{"text": text, "spk": -1}] if text else []
    except Exception as e:
        log(f"[baize-asr] 识别异常: {e}")
    return []


def extract_spk_embedding(pcm: bytes):
    """抽单句说话人 embedding（L2 归一化）；无模型 / 音频过短返回 None。

    用独立加载的 cam++ 向量模型，对一段 finalized 音频取 192 维说话人向量，
    供服务端自建跨句聚类使用（FunASR 自带 spk 字段无法跨句稳定区分）。"""
    if spk_emb_model is None or len(pcm) < 16000 // 2:   # 短于 0.5s 不抽
        return None
    samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    try:
        r = spk_emb_model.generate(input=samples, fs=16000)
        spk = r[0].get("spk_embedding")
        if hasattr(spk, "detach"):            # torch.Tensor(cuda/cpu) → numpy（GPU 必须先 .cpu()）
            spk = spk.detach().cpu().numpy()
        emb = np.asarray(spk, dtype=np.float64).reshape(-1)
        n = np.linalg.norm(emb)
        if n < 1e-6:
            return None
        return emb / n
    except Exception as e:
        log(f"[baize-asr] 说话人向量抽取失败: {e}")
        return None


def recognize_stream(pcm: bytes, cache: dict) -> str:
    """流式在线识别：输入一块音频（约 600ms），返回当前时刻累计的中间文本。
    cache 由 FunASR 就地维护，同一句话持续传同一个 dict；句子结束后丢弃重建。
    返回文本可随新音频增长/修正，客户端做原位覆盖显示。"""
    if stream_model is None:
        return ""
    samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    if samples.size == 0:
        return ""
    try:
        res = stream_model.generate(input=samples, cache=cache, is_final=False,
                                    chunk_size=[0, 10, 5], encoder_chunk_look_back=4,
                                    decoder_chunk_look_back=1)
        if res and len(res) > 0 and res[0].get("text") is not None:
            return res[0]["text"]
    except Exception as e:
        log(f"[baize-asr] 流式识别异常: {e}")
    return ""


def assign_speaker(pcm: bytes, centroids: list) -> int:
    """抽本句说话人 embedding，比对已有质心分配稳定编号；新人则新建质心。
    返回 -1 表示无说话人模型 / 音频过短（回落"我（本机）"）。
    centroids 为可变 list（L2 归一化 embedding），跨句稳定聚类，由各连接持有。

    稳健策略：短句(<2s) embedding 噪声大 → 绝不因短句新建说话人；
    灰区相似度 → 宁并勿拆（归入最像者但不更新质心，避免污染）；
    只有"长句 + 与所有质心都明显不像"才认定为新说话人。"""
    emb = extract_spk_embedding(pcm)
    if emb is None:
        return -1
    if not centroids:
        centroids.append(emb)
        return 0
    sims = [float(np.dot(emb, c)) for c in centroids]
    best = int(np.argmax(sims))
    if sims[best] >= SPK_SIM_SAME:
        c = 0.9 * centroids[best] + 0.1 * emb   # 质心指数滑动平均，抗音量/通道抖动
        centroids[best] = c / np.linalg.norm(c)
        return best
    # 灰区/低相似：短句一律并入最像者；长句且确实谁都不像才新建
    if len(pcm) < int(16000 * SPK_MIN_NEW_SEC) * 2 or sims[best] >= SPK_SIM_MERGE:
        return best
    centroids.append(emb)
    log(f"[说话人] 新建说话人 {len(centroids) - 1}（当前共 {len(centroids)} 人）")
    return len(centroids) - 1


# ---- 重叠语音（两人同时说话 / 抢话）的处理 ----
# 为什么不做"检测到重叠才分离"：实测混合物并*不*表现为"和谁都不像"，而是被整体判给
# 能量占优的那一方（等权重叠 vs 质心B = 0.604，照样能正常聚类），所以声纹层面拿不到
# 可用的重叠信号。只能每条句子都试一次分离，再从结果上判定到底有没有第二个人。


def separate_overlap(pcm: bytes):
    """把可能含两人叠加的一句话拆成两路 16bit PCM；未启用 / 音频过短 / 失败返回 None。"""
    if sep_session is None or len(pcm) < int(16000 * SEP_MIN_SEC) * 2:
        return None
    samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    try:
        outs = sep_session.run(None, {"inputs": samples[None, :]})
    except Exception as e:
        log(f"[baize-asr] 语音分离失败，回落整句识别: {e}")
        return None
    res = []
    for o in outs:
        x = np.clip(np.asarray(o)[0], -1.0, 1.0)
        res.append((x * 32767.0).astype(np.int16).tobytes())
    return res


def argmax_speaker(pcm: bytes, centroids: list):
    """把一段音频分配给已有说话人中**最像**的那个（只看相对排序），不新建也不更新质心。

    专供分离后的语音使用。为什么不复用 assign_speaker 的阈值逻辑：分离出的波形是模型重建的，
    cam++ 从上面抽的 embedding 与原始句子的相似度**绝对值明显偏低**（实测只有 0.12~0.19，
    远低于 SPK_SIM_MERGE=0.31），套绝对阈值会一律判成"新人"；但它的 argmax 排序是可靠的。
    且这类 embedding 不用于更新质心，避免把重建产物混进原始声纹档案。"""
    emb = extract_spk_embedding(pcm)
    if emb is None or not centroids:
        return None
    sims = [float(np.dot(emb, c)) for c in centroids]
    return int(np.argmax(sims))


def _recognize_overlap(est_pcm: list, centroids: list):
    """分离出的两路各自识别并归属说话人。

    返回 (items, same_spk)：
    - items 为 None：某一路没有文本 / 归属失败，分离结果不可用；
    - items 非空且 same_spk=False：真重叠，两路分属不同人，直接采用；
    - items 非空且 same_spk=True：两路都识别出了内容但归到同一个人 —— 可能质心不准
      （真实重叠被误判），也可能是单人被硬拆。交给调用方结合"混音识别是否为空"裁决。"""
    items = []
    for pcm in est_pcm:
        sid = argmax_speaker(pcm, centroids)
        if sid is None:
            return None, False
        text = "".join(s["text"] for s in recognize(pcm)).strip()
        if not text:
            return None, False
        items.append({"text": text, "spk": sid})
    return items, items[0]["spk"] == items[1]["spk"]


def finalize_utterance(pcm: bytes, centroids: list) -> list[dict]:
    """一句话 → [{"text", "spk"}]：真的有两个人同时说话时返回两条，否则一条。
    阻塞调用，放线程池执行。

    关键裁决顺序（避免单人句被分离器污染）：
    1. 先对**混音**做单人识别。能出字就一定是单人说话（或重叠被 paraformer 顺带解码），
       直接采用——绝不把单人句丢进分离器产出伪影两路。这是 99% 的场景，速度和没启用
       分离时完全一致，且质量等同于纯 2pass 基线。
    2. 只有混音识别为空（真重叠的强特征：paraformer 解不开两人叠音）且已认出 ≥2 个说话人时，
       才去试分离；分离出的两路分属不同人 → 真实重叠，保留两路；否则宁可不出也不吐伪影。"""
    # 单人路径（同时充当裁决依据）：混音能识别出内容就是单人说话。
    spk_id = assign_speaker(pcm, centroids)
    singles = [{"text": s["text"], "spk": spk_id} for s in recognize(pcm)]
    if singles:
        return singles
    # 混音为空：可能是真重叠，也可能是纯静音/噪声。只在已建出 ≥2 个说话人时试分离，
    # 避免对单人句白烧 0.7 RTF 的分离开销。
    if len(centroids) < 2:
        return []
    est = separate_overlap(pcm)
    if not est:
        return []
    items, same_spk = _recognize_overlap(est, centroids)
    if items is not None and not same_spk:
        log("[说话人] 混音为空且两路分属不同人 → 真实重叠，保留两路")
        # 模型的 spk0/spk1 输出口与说话人编号无关，按编号排好再回传，客户端显示顺序才稳定
        return sorted(items, key=lambda x: x["spk"])
    if items is not None:
        log(f"[说话人] 两路归属同一人({items[0]['spk']}) → "
            f"「{items[0]['text']}」/「{items[1]['text']}」")
    else:
        log("[说话人] 分离无有效两路文本")
    # 兜底：混音空 + 分离不可用 → 宁可不出，也不吐伪影。落盘供离线分析。
    capture_pcm(pcm, "mix_empty")
    return []


async def flush(ws, buf: bytearray, spk_centroids: list):
    """把缓冲区音频识别后回传 final；说话人编号由自建跨句聚类决定（覆盖 FunASR 单次调用内聚类）。
    启用分离且确实检出两人时，这一句会回传两条，各自带自己的说话人编号。"""
    if len(buf) >= MIN_UTTERANCE_BYTES:
        t0 = time.monotonic()
        items = await asyncio.to_thread(finalize_utterance, bytes(buf), spk_centroids)
        cost = time.monotonic() - t0
        log(f"[识别] 输入 {len(buf)/32000:.1f}s → {len(items)} 条，耗时 {cost:.2f}s")
        for item in items:
            await ws.send(json.dumps(
                {"mode": "offline", "text": item["text"], "spk": item["spk"], "is_final": True},
                ensure_ascii=False))
    buf.clear()


async def handle(ws):
    peer = ws.remote_address
    buf = bytearray()                 # 本句累计音频（供离线定稿识别）
    pending = bytearray()             # 尚未喂给流式模型的新音频
    stream_cache: dict = {}           # 当前句子的流式状态；句子结束即丢弃重建
    online_text = ""
    has_voice = False
    agc_gain = 1.0
    last_voice = time.monotonic()
    # 接收统计（诊断麦克风链路）：每 5 秒汇报一次收字节量 / 峰值 RMS
    stat_bytes = 0
    stat_peak_rms = 0.0
    stat_voice_blocks = 0
    last_stat = time.monotonic()
    logged_connect = False            # 仅在真正收到数据后才打印"连接"，过滤启动探活噪声
    spk_centroids: list = []          # 各说话人 embedding 质心（L2 归一化），跨句稳定聚类用

    async def finalize():
        """一句话说完：离线模型定稿（带标点 + 说话人），并重置流式状态。"""
        nonlocal online_text, stream_cache, has_voice
        await flush(ws, buf, spk_centroids)
        buf.clear()
        pending.clear()
        stream_cache = {}
        online_text = ""
        has_voice = False

    try:
        async for msg in ws:
            if not logged_connect:
                logged_connect = True
                log(f"[baize-asr] 连接: {peer}")
            if isinstance(msg, str):
                try:
                    cfg = json.loads(msg)
                except json.JSONDecodeError:
                    continue
                if cfg.get("is_speaking") is False:
                    await finalize()
                continue

            # 自动增益：小声也能稳定过 VAD 阈值（静音段不放大）
            if isinstance(msg, (bytes, bytearray)):
                msg, agc_gain = apply_agc(bytes(msg), agc_gain)
            buf.extend(msg)
            pending.extend(msg)
            stat_bytes += len(msg)
            # 能量 VAD：本块是否含语音
            usable = msg[: len(msg) // 2 * 2]
            if usable:
                samples = np.frombuffer(usable, dtype=np.int16).astype(np.float32) / 32768.0
                rms = float(np.sqrt(np.mean(samples ** 2)))
                stat_peak_rms = max(stat_peak_rms, rms)
                if rms > RMS_THRESHOLD:
                    last_voice = time.monotonic()
                    has_voice = True
                    stat_voice_blocks += 1

            # ---- online 流式：边说边出字（每 600ms chunk 增量识别）----
            while has_voice and len(pending) >= STREAM_CHUNK_BYTES:
                chunk = bytes(pending[:STREAM_CHUNK_BYTES])
                del pending[:STREAM_CHUNK_BYTES]
                text = await asyncio.to_thread(recognize_stream, chunk, stream_cache)
                if text and text != online_text:
                    online_text = text
                    await ws.send(json.dumps(
                        {"mode": "online", "text": text, "is_final": False},
                        ensure_ascii=False))

            # 未说话时不要把静音一直堆在缓冲区里（既拖慢离线识别也没意义）
            if not has_voice:
                if len(buf) > SILENCE_KEEP_BYTES:
                    del buf[: len(buf) - SILENCE_KEEP_BYTES]
                if len(pending) > STREAM_CHUNK_BYTES:
                    del pending[: len(pending) - STREAM_CHUNK_BYTES]

            # 自适应切句：连续说话越久，允许的结束静音越短（换气即可切），避免长憋才出字
            voiced = len(buf) / 32000.0          # 当前缓冲区时长（16k/16bit/mono）
            silence_ms = SILENCE_MS_LONG if voiced >= LONG_UTTERANCE_SEC else SILENCE_MS
            if has_voice and (time.monotonic() - last_voice) * 1000 > silence_ms:
                await finalize()
            # 硬切兜底：说话完全不停时也要出字，避免缓冲无限增长 / 一次性识别过久
            elif len(buf) >= MAX_UTTERANCE_BYTES:
                await finalize()

            # 每 5 秒一条接收统计：字节涨 = 麦克风链路通；峰值 RMS 判断是否静音
            if time.monotonic() - last_stat >= 5:
                last_stat = time.monotonic()
                level = ("有声" if stat_voice_blocks > 0
                         else "静音" if stat_peak_rms < 0.005 else "音量偏低")
                log(f"[统计] 收到 {stat_bytes/1024:.0f} KB | 峰值RMS {stat_peak_rms:.4f} | "
                    f"语音块 {stat_voice_blocks} | 缓冲 {len(buf)/1024:.0f} KB | {level}")
    except websockets.ConnectionClosed:
        pass
    except Exception as ex:
        import traceback as _tb
        log("[baize-asr] handle 异常: " + _tb.format_exc())
    finally:
        if logged_connect:
            log(f"[baize-asr] 断开: {peer}（共收 {stat_bytes/1024:.0f} KB，峰值RMS {stat_peak_rms:.4f}）")
        else:
            log_debug(f"[baize-asr] 探测连接(无音频，已静默): {peer}")


async def main():
    async with websockets.serve(handle, args.host, args.port, max_size=None):
        log(f"[baize-asr] WebSocket 服务就绪: ws://{args.host}:{args.port}")
        await asyncio.Future()  # 永久运行


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
