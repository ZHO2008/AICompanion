# Copyright (c) 2024 Alibaba Inc (authors: Xiang Lyu)
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#   http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
import os
import sys
import time
import argparse
import logging
import tempfile
import torch
logging.getLogger('matplotlib').setLevel(logging.WARNING)
from fastapi import FastAPI, UploadFile, Form, File
from fastapi.responses import StreamingResponse
from fastapi.middleware.cors import CORSMiddleware
import uvicorn
import numpy as np
ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.append('{}/../../..'.format(ROOT_DIR))
sys.path.append('{}/../../../third_party/Matcha-TTS'.format(ROOT_DIR))
from cosyvoice.cli.cosyvoice import AutoModel
from cosyvoice.utils.file_utils import load_wav

app = FastAPI()
# set cross region allowance
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"])


def generate_data(model_output):
    for i in model_output:
        tts_audio = (i['tts_speech'].numpy() * (2 ** 15)).astype(np.int16).tobytes()
        yield tts_audio


# ---- 本地补丁（非上游代码）------------------------------------------------
# 上游 server.py 先把上传的 wav 用 load_wav() 读成 tensor 再传给 inference_*，
# 而 cosyvoice/cli/frontend.py 内部的 load_wav() 只接受路径/文件对象，会再调一次
# torchaudio.load，于是抛 "TypeError: Invalid file: tensor([...])"。
# 改成把上传内容落到临时文件、传路径进去：前端就能按 24 kHz（频谱）与 16 kHz（token /
# speaker embedding）分别正确重采样，而不是把 16k 张量再上采样一次。
async def _save_upload(upload: UploadFile) -> str:
    data = await upload.read()
    fd, path = tempfile.mkstemp(suffix='.wav')
    with os.fdopen(fd, 'wb') as f:
        f.write(data)
    return path


def _stream_and_cleanup(model_output, path):
    try:
        for chunk in generate_data(model_output):
            yield chunk
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


@app.get("/inference_sft")
@app.post("/inference_sft")
async def inference_sft(tts_text: str = Form(), spk_id: str = Form()):
    model_output = cosyvoice.inference_sft(tts_text, spk_id)
    return StreamingResponse(generate_data(model_output))


@app.get("/inference_zero_shot")
@app.post("/inference_zero_shot")
async def inference_zero_shot(tts_text: str = Form(), prompt_text: str = Form(), prompt_wav: UploadFile = File()):
    path = await _save_upload(prompt_wav)
    return StreamingResponse(_stream_and_cleanup(cosyvoice.inference_zero_shot(tts_text, prompt_text, path), path))


@app.get("/inference_cross_lingual")
@app.post("/inference_cross_lingual")
async def inference_cross_lingual(tts_text: str = Form(), prompt_wav: UploadFile = File()):
    path = await _save_upload(prompt_wav)
    return StreamingResponse(_stream_and_cleanup(cosyvoice.inference_cross_lingual(tts_text, path), path))


@app.get("/inference_instruct")
@app.post("/inference_instruct")
async def inference_instruct(tts_text: str = Form(), spk_id: str = Form(), instruct_text: str = Form()):
    model_output = cosyvoice.inference_instruct(tts_text, spk_id, instruct_text)
    return StreamingResponse(generate_data(model_output))


@app.get("/inference_instruct2")
@app.post("/inference_instruct2")
async def inference_instruct2(tts_text: str = Form(), instruct_text: str = Form(), prompt_wav: UploadFile = File()):
    path = await _save_upload(prompt_wav)
    return StreamingResponse(_stream_and_cleanup(cosyvoice.inference_instruct2(tts_text, instruct_text, path), path))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port',
                        type=int,
                        default=50000)
    parser.add_argument('--model_dir',
                        type=str,
                        default='iic/CosyVoice2-0.5B',
                        help='local path or modelscope repo id')
    # 本地补丁（非上游代码）：GPU 上开启 fp16 autocast，实测 RTF 1.3 → 1.0。
    # -1 = 自动（有 CUDA 就开），0 = 强制关，1 = 强制开。
    parser.add_argument('--fp16',
                        type=int,
                        default=-1)
    # 本地补丁（非上游代码）：启动时先跑一句废弃的合成，把 CUDA kernel / ORT EP 的
    # 一次性初始化（实测 20-70 秒）提前付掉，这样第一句用户请求就是 4-5 秒。
    parser.add_argument('--warmup',
                        type=int,
                        default=1)
    parser.add_argument('--warmup_wav',
                        type=str,
                        default='asset/zero_shot_prompt.wav')
    args = parser.parse_args()

    use_fp16 = torch.cuda.is_available() if args.fp16 < 0 else bool(args.fp16)
    if use_fp16 and not torch.cuda.is_available():
        logging.warning('指定了 fp16 但没有 CUDA，已回退 fp32。')
        use_fp16 = False
    logging.info('CosyVoice 启动：fp16=%s, cuda=%s', use_fp16, torch.cuda.is_available())

    cosyvoice = AutoModel(model_dir=args.model_dir, fp16=use_fp16)

    if args.warmup and os.path.exists(args.warmup_wav):
        t0 = time.time()
        try:
            for _ in cosyvoice.inference_zero_shot(
                    '你好，我是你的桌面伙伴，这是一次启动预热，请忽略这段声音。',
                    'You are a helpful assistant.<|endofprompt|>今天天气不错。',
                    args.warmup_wav, stream=False):
                pass
            logging.info('预热完成，用时 %.1fs（之后的首句请求不再付这次开销）', time.time() - t0)
        except Exception as ex:
            # 预热失败不影响服务，只是首句会慢。
            logging.warning('预热失败（忽略，首句会变慢）：%s', ex)

    uvicorn.run(app, host="0.0.0.0", port=args.port)
