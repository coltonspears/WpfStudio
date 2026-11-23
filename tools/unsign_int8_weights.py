"""Stores a dynamically quantized ONNX model's int8 MatMulInteger weights as uint8, so it computes the same on every x64 CPU.

    python unsign_int8_weights.py <model.int8.onnx> <model.onnx>

onnxruntime.quantization.quantize_dynamic writes int8 weights, so every MatMulInteger multiplies uint8 activations by int8
weights (U8S8). On x64 CPUs without VNNI, ONNX Runtime's U8S8 kernel adds each pair of products with VPMADDUBSW, which
saturates at 16 bits, while CPUs with AVX-VNNI, AVX512-VNNI or AMX add them exactly. So the same file scored lines
differently from one CPU to the next. Storing each weight w as w + 128 with a zero point of z + 128 leaves every
(a - za) * (w - z) as it was, and ONNX Runtime's uint8-by-uint8 kernels add the products in at least 32 bits on every CPU.
"""
import sys

import numpy as np
import onnx
from onnx import TensorProto, numpy_helper


def unsign(model: onnx.ModelProto) -> int:
    weights = {node.input[i] for node in model.graph.node if node.op_type == "MatMulInteger" for i in (1, 3) if len(node.input) > i and node.input[i]}
    signed = [init for init in model.graph.initializer if init.data_type == TensorProto.INT8]
    others = [init.name for init in signed if init.name not in weights]
    if others:
        raise SystemExit(f"int8 initializers outside MatMulInteger weights and zero points: {others}")
    # A weight left without a zero point has zero as its zero point, which as uint8 would have to be stated as 128.
    names = {init.name for init in signed}
    for node in model.graph.node:
        if node.op_type == "MatMulInteger" and node.input[1] in names and (len(node.input) < 4 or not node.input[3]):
            raise SystemExit(f"{node.name} has int8 weights and no zero point; add one before converting.")
    for init in signed:
        values = numpy_helper.to_array(init)
        init.CopyFrom(numpy_helper.from_array((values.astype(np.int16) + 128).astype(np.uint8), init.name))
    onnx.checker.check_model(model)
    return len(signed)


if __name__ == "__main__":
    source, target = sys.argv[1], sys.argv[2]
    model = onnx.load(source)
    count = unsign(model)
    onnx.save(model, target)
    print(f"{target}: {count} int8 initializers stored as uint8")
