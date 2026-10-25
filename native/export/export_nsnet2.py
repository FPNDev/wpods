# Graph surgery on Microsoft's NSNet2 DNS-Challenge baseline ONNX models (nsnet2-20ms-baseline.onnx = 16 kHz,
# nsnet2-20ms-48k-baseline.onnx = 48 kHz) for frame-by-frame streaming in Audio\Onnx\NsNet2Stage.cs.
# Upstream the two GRU layers start every call from zeros (initial_h = ConstantOfShape) and keep their final state
# internal. Here each GRU's initial_h becomes graph input h<i>_in and its Y_h output becomes graph output h<i>_out
# (shape [1, 1, hidden]), the nodes that only built the zero state are removed, and all shapes are made static for one
# frame per call (input / output [1, 1, bins]). Weights and every other node are unchanged.
# Check: the streamed model (one frame per call, state fed back) reproduces the upstream model run on the whole
# sequence.
# Usage: python export_nsnet2.py <upstream .onnx> <out .onnx>
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
    state_inputs = []
    state_outputs = []
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
        state_inputs.append(helper.make_tensor_value_info(name_in, TensorProto.FLOAT, [1, 1, hidden]))
        state_outputs.append(helper.make_tensor_value_info(name_out, TensorProto.FLOAT, [1, 1, hidden]))

    for value in (graph.input[0], graph.output[0]):
        dims = value.type.tensor_type.shape.dim
        del dims[:]
        for size in (1, 1, bins):
            dims.add().dim_value = size
    graph.input.extend(state_inputs)
    graph.output.extend(state_outputs)

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
    onnx.save(model, out_path)

    # Check against the upstream model on a 200-frame log-power feature sequence.
    rng = np.random.default_rng(0)
    frames = 200
    feat = np.log10(np.maximum((rng.standard_normal((1, frames, bins)) * 0.1) ** 2, 1e-12)).astype(np.float32)
    feat[:, 50:150, :bins // 4] += 4
    full = ort.InferenceSession(src).run(None, {"input": feat})[0]
    stream = ort.InferenceSession(out_path)
    state = [np.zeros(s.shape, np.float32) for s in stream.get_inputs()[1:]]
    names = [s.name for s in stream.get_inputs()]
    worst = 0.0
    for t in range(frames):
        result = stream.run(None, dict(zip(names, [feat[:, t:t + 1, :]] + state)))
        state = result[1:]
        worst = max(worst, float(np.abs(result[0][0, 0] - full[0, t]).max()))
    print(f"{out_path}: {bins} bins, GRU states {[s.shape for s in state]}, max |stream - upstream| {worst:.2e}")


if __name__ == "__main__":
    main()
