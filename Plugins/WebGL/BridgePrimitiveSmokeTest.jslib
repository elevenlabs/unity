// Smoke-test math factory for Phase 4 WebGL integration testing.
// Called from BridgePrimitiveSmokeTest.cs via EL_SmokeTest_RegisterMathFactory().
// Exercises every bridge primitive surface: sync/async methods, property read,
// BridgeCallback fan-out, and function-handle round-trip via removeListener.

mergeInto(LibraryManager.library, {
    EL_SmokeTest_RegisterMathFactory__deps: ["$EL_RegisterFactory"],
    EL_SmokeTest_RegisterMathFactory: function () {
        var listeners = [];
        _EL_RegisterFactory("mathFactory", function () {
            return {
                add: function (a, b) {
                    return a + b;
                },
                addAsync: function (a, b) {
                    return Promise.resolve(a + b);
                },
                pi: Math.PI,
                addTickListener: function (cb) {
                    listeners.push(cb);
                    return function () {
                        var idx = listeners.indexOf(cb);
                        if (idx >= 0) listeners.splice(idx, 1);
                    };
                },
                invokeTick: function (value) {
                    listeners.slice().forEach(function (cb) {
                        cb(value);
                    });
                },
                // V1: fires all listeners from a Promise microtask (async JS context).
                invokeTickFromPromise: function (value) {
                    return Promise.resolve().then(function () {
                        listeners.slice().forEach(function (cb) {
                            cb(value);
                        });
                    });
                },
                // V3: fires all listeners `count` times with sequential values 0..count-1.
                invokeTickN: function (count) {
                    var lns = listeners.slice();
                    for (var i = 0; i < count; i++) {
                        for (var j = 0; j < lns.length; j++) {
                            lns[j](i);
                        }
                    }
                },
                // V4: returns a JS function that adds `n` to its argument.
                makeAdder: function (n) {
                    return function (x) {
                        return n + x;
                    };
                },
            };
        });
    },
});
