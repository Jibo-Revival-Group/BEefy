#!/usr/bin/env python3
"""One-time export; inference does not depend on torch/transformers."""
import argparse
from pathlib import Path
import shutil

import torch
from transformers import BertForMaskedLM
from onnxruntime.quantization import quantize_dynamic, QuantType


class LogitsOnly(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids, attention_mask, token_type_ids):
        return self.model(
            input_ids=input_ids,
            attention_mask=attention_mask,
            token_type_ids=token_type_ids,
        ).logits


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    root = args.directory
    model = BertForMaskedLM.from_pretrained(
        str(root / "source"), local_files_only=True
    ).eval()
    ids = torch.tensor([[101, 2054, 2051, 2003, 2009, 102]])
    output = root / "model"
    (output / "onnx").mkdir(parents=True, exist_ok=True)
    names = ["input_ids", "attention_mask", "token_type_ids"]
    floating = output / "onnx/model.onnx"
    torch.onnx.export(
        LogitsOnly(model),
        (ids, torch.ones_like(ids), torch.zeros_like(ids)),
        str(floating),
        input_names=names,
        output_names=["logits"],
        dynamic_axes={
            name: {0: "batch_size", 1: "sequence_length"} for name in names + ["logits"]
        },
        opset_version=17,
        dynamo=False,
    )
    quantized = output / "onnx/model_quantized.onnx"
    quantize_dynamic(str(floating), str(quantized), weight_type=QuantType.QInt8)
    shutil.copy(root / "source/config.json", output / "config.json")
    floating.unlink()
    print(f"Exported contextual weights: {quantized.stat().st_size} bytes")


if __name__ == "__main__":
    main()
