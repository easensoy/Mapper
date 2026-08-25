# Gate model fixtures

The twins `gate.fixtures.json` compiles by default, one folder per model, each holding the
`Control.xml` that model's VueOne project exports.

They are checked in so a gate run is reproducible on a machine that is not the one it was
authored on: the same input produces the same 8 combinations everywhere.

To gate the twins you are actually editing instead, point the run at them:

    VUEONE_MODELS=<path containing the model folders>

That overrides `modelsRoot` and nothing else, so the model names, the target selections and the
baseline project stay as the manifest declares them.
