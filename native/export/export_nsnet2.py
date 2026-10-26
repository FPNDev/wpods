# Graph surgery on Microsoft's NSNet2 DNS-Challenge baseline ONNX models (nsnet2-20ms-baseline.onnx = 16 kHz,
# nsnet2-20ms-48k-baseline.onnx = 48 kHz) for frame-by-frame streaming in Audio\Onnx\NsNet2Stage.cs.
# Upstream the two GRU layers start every call from zeros (initial_h = ConstantOfShape) and keep their final state
# internal. Here each GRU's initial_h becomes graph input h<i>_in and its Y_h output becomes graph output h<i>_out
# (shape [1, 1, hidden]), the nodes that only built the zero state are removed, and all shapes are made static for one
# frame per call (input / output [1, 1, bins]). Weights and every other node are unchanged.
# Check: the streamed model (one frame per call, state fed back) reproduces the upstream model run on the whole
# sequence (max abs difference above 1e-4 fails the export; out .onnx is written only after the check passed).
# Usage: python export_nsnet2.py <upstream .onnx> <out .onnx>
import os
import sys

import numpy as np
import onnx
import onnxruntime as ort
from onnx import TensorProto, helper


def main():
    src, out_path = sys.argv[1], sys.argv[2]
    model = onnx.load(src)
    graph = model.graph
    bins = graph.input[0].type.tensor_type.shape.dim[2].dim_value
    grus = [n for n in graph.node if n.op_type == "GRU"]
    for i, gru in enumerate(grus):
        hidden = next(a.i for a in gru.attribute if a.name == "hidden_size")
        name_in, name_out = f"h{i}_in", f"h{i}_out"
        gru.input[5] = name_in
        old = gru.output[1]
        for node in graph.node:
            for k, value in enumerate(node.input):
                if value == old:
                    node.input[k] = name_out
        gru.output[1] = name_out
        graph.input.append(helper.make_tensor_value_info(name_in, TensorProto.FLOAT, [1, 1, hidden]))
        graph.output.append(helper.make_tensor_value_info(name_out, TensorProto.FLOAT, [1, 1, hidden]))

    for value in (graph.input[0], graph.output[0]):
        dims = value.type.tensor_type.shape.dim
        del dims[:]
        for size in (1, 1, bins):
            dims.add().dim_value = size

    # Drop nodes whose outputs nothing uses any more (the zero-state builders).
    while True:
        used = {name for node in graph.node for name in node.input} | {o.name for o in graph.output}
        dead = [n for n in graph.node if not any(o in used for o in n.output)]
        if not dead:
            break
        for node in dead:
            graph.node.remove(node)
    used = {name for node in graph.node for name in node.input}
    for init in [t for t in graph.initializer if t.name not in used]:
        graph.initializer.remove(init)
    del graph.value_info[:]
    onnx.checker.check_model(model)
    # Saved under a temporary name and renamed only after the check passed: export-models.ps1 skips the export when
    # out_path exists, so out_path must never hold an unchecked model.
    tmp_path = out_path + ".tmp"
    onnx.save(model, tmp_path)
    try:
        # Check against the upstream model on a 200-frame log-power feature sequence.
        rng = np.random.default_rng(0)
        frames = 200
        feat = np.log10(np.maximum((rng.standard_normal((1, frames, bins)) * 0.1) ** 2, 1e-12)).astype(np.float32)
        feat[:, 50:150, :bins // 4] += 4
        full = ort.InferenceSession(src).run(None, {"input": feat})[0]
        stream = ort.InferenceSession(tmp_path)
        state = [np.zeros(s.shape, np.float32) for s in stream.get_inputs()[1:]]
        names = [s.name for s in stream.get_inputs()]
        worst = 0.0
        for t in range(frames):
            result = stream.run(None, dict(zip(names, [feat[:, t:t + 1, :]] + state)))
            state = result[1:]
            worst = max(worst, float(np.abs(result[0][0, 0] - full[0, t]).max()))
        print(f"{out_path}: {bins} bins, GRU states {[s.shape for s in state]}, max |stream - upstream| {worst:.2e}")
        if worst > 1e-4:
            sys.exit(f"{out_path}: streamed model does not match upstream, mismatch {worst:.2e}")
        os.replace(tmp_path, out_path)
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)


if __name__ == "__main__":
    main()
