"""Makes native\\deps\\colour.onnx: DDColor-tiny (Apache-2.0) for a small, fixed picture size.

The passthrough's colour guess (see vws_colour.h) runs this on the processor, a few times a
second, on a small copy of the camera's picture. The ONNX files of the model that are published
are for 512x512 with half-precision weights -- over a second a picture on a processor -- so this
exports the same network from the authors' own code and weights at 256x256 (or the size given),
in single precision.

    python native\\export-colour-model.py <DDColor source folder> <pytorch_model.bin> <out.onnx> [size]

  source:  github.com/piddnad/DDColor at commit 2adb63f2656ac41cbdf7b894cddd94121a3faf13
  weights: huggingface.co/piddnad/ddcolor_paper_tiny, pytorch_model.bin
           SHA-256 8a1277bc90a1bfbb6d2d83933a9a6bc821931879ca93e26e4fcec12165d41fce

Needs torch and onnx. In: (1, 3, size, size), the grey picture three times over, 0..1.
Out: (1, 2, size, size), CIE Lab's a and b.
"""
import hashlib
import os
import sys
import types

import torch

WEIGHTS_SHA256 = '8a1277bc90a1bfbb6d2d83933a9a6bc821931879ca93e26e4fcec12165d41fce'


def packages(source):
    """The architecture's modules without the training framework around them."""
    for name, folder in (('basicsr', 'basicsr'), ('basicsr.archs', 'basicsr/archs'), ('basicsr.utils', 'basicsr/utils'),
                         ('basicsr.archs.ddcolor_arch_utils', 'basicsr/archs/ddcolor_arch_utils')):
        module = types.ModuleType(name)
        module.__path__ = [os.path.join(source, folder)]
        sys.modules[name] = module

    class Registry:
        def register(self, *args, **kwargs):
            return lambda cls: cls

    registry = types.ModuleType('basicsr.utils.registry')
    registry.ARCH_REGISTRY = Registry()
    sys.modules['basicsr.utils.registry'] = registry


def main():
    source, weights, out = sys.argv[1], sys.argv[2], sys.argv[3]
    size = int(sys.argv[4]) if len(sys.argv) > 4 else 256

    with open(weights, 'rb') as f:
        digest = hashlib.sha256(f.read()).hexdigest()

    if digest != WEIGHTS_SHA256:
        sys.exit('the weights are not the file expected (SHA-256 %s)' % digest)

    packages(source)
    from basicsr.archs.ddcolor_arch import DDColor

    model = DDColor(encoder_name='convnext-t', decoder_name='MultiScaleColorDecoder', input_size=(size, size), num_output_channels=2,
                    last_norm='Spectral', do_normalize=False, num_queries=100, num_scales=3, dec_layers=9)
    state = torch.load(weights, map_location='cpu', weights_only=True)
    state = state.get('params', state)
    state = {(k[len('model.'):] if k.startswith('model.') else k): v for k, v in state.items()}
    missing, unexpected = model.load_state_dict(state, strict=False)

    if missing or unexpected:
        sys.exit('the weights do not fit the network: missing %s, unexpected %s' % (missing[:5], unexpected[:5]))

    model.eval()
    picture = torch.rand(1, 3, size, size)

    with torch.no_grad():
        before = model(picture)
        torch.onnx.export(model, (picture,), out, input_names=['input'], output_names=['output'], opset_version=17, dynamo=False,
                          do_constant_folding=True)

    import numpy as np
    import onnxruntime as ort

    session = ort.InferenceSession(out, providers=['CPUExecutionProvider'])
    after = session.run(None, {'input': picture.numpy()})[0]
    print('%s: %dx%d, %.1f MB; the exported file differs from the network by at most %.4f (a and b run to about +-100)' % (
        out, size, size, os.path.getsize(out) / 1e6, float(np.abs(after - before.numpy()).max())))


if __name__ == '__main__':
    main()
