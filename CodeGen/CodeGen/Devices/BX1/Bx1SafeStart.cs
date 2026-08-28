using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Devices.BX1
{
    // THE ONE RESOLVED COVER SAFE-START.
    //
    // The injector wires a Bx1CoverFailsafe gate to force one actuator HOME on start; the scanner
    // validator prints the coupler fallback word that keeps it homed on Clean/Stop/fault. Those are
    // two statements about the SAME coil, and they used to be derived twice - the injector from
    // literal bit numbers, the validator from the declaration - so a re-bitted or renamed coil would
    // silently make the printed instruction disagree with the emitted logic, on a safety gate.
    //
    // Both now read this. It is resolved from device.yml's bx1Io block alone (no FB, no file), so the
    // validator can quote it without a deployed project and the injector cannot wire a different one.
    public sealed class Bx1SafeStart
    {
        // One declared coil, and which actuator drives it in which direction.
        public readonly record struct Coil(string Component, Configuration.Bx1Signal Signal, bool DrivesHome);

        public string Component { get; }
        public Configuration.Bx1Signal CoilToWork { get; }
        public Configuration.Bx1Signal CoilToHome { get; }
        public Configuration.Bx1Signal SensorFromHome { get; }

        // Every declared coil in word-bit order - what the fallback word is spelled out against.
        public IReadOnlyList<Coil> Coils { get; }

        // The coils of every OTHER actuator, in word-bit order. The gate holds these off while it is
        // homing, so nothing else can be energised into the volume the safe actuator is crossing.
        public IReadOnlyList<Coil> HeldOff { get; }

        // The coupler output-fallback word that leaves ONLY the safe actuator's home coil energised.
        public int FallbackWord => 1 << CoilToHome.Bit;

        private Bx1SafeStart(string component, Configuration.Bx1Signal toWork,
            Configuration.Bx1Signal toHome, Configuration.Bx1Signal atHome,
            IReadOnlyList<Coil> coils, IReadOnlyList<Coil> heldOff)
        {
            Component = component; CoilToWork = toWork; CoilToHome = toHome; SensorFromHome = atHome;
            Coils = coils; HeldOff = heldOff;
        }

        // FAIL CLOSED. A safe-start that cannot be resolved is not a safe-start: emitting the gate
        // anyway would wire a pin to nothing and the actuator would be free to energise Work at power-on.
        public static Bx1SafeStart Resolve(Configuration.CompilerConfiguration cfg) =>
            Resolve((cfg ?? throw new ArgumentNullException(nameof(cfg))).Devices.Bx1Io);

        public static Bx1SafeStart Resolve(Configuration.Bx1IoProfile io)
        {
            if (io == null) throw new ArgumentNullException(nameof(io));
            var name = (io.SafeStartComponent ?? string.Empty).Trim();
            if (name.Length == 0)
                throw new InvalidOperationException(
                    "device.yml bx1Io declares no safeStartComponent, so nothing states which actuator " +
                    "must be forced home on start.");

            var matches = io.Covers
                .Where(c => string.Equals(c.Component, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"device.yml bx1Io.safeStartComponent '{name}' matches {matches.Count} declared covers; " +
                    "exactly one must own the safe-start.");

            var safe = matches[0];
            if (safe.CoilToWork == null || safe.CoilToHome == null)
                throw new InvalidOperationException(
                    $"bx1Io.safeStartComponent '{name}' declares " +
                    (safe.CoilToHome == null ? "no coilToHome" : "no coilToWork") +
                    ". A safe-start has to DRIVE the actuator home and hold its work coil off, so it " +
                    "can only be a double-acting actuator that declares both coils.");
            if (safe.SensorFromHome == null)
                throw new InvalidOperationException(
                    $"bx1Io.safeStartComponent '{name}' declares no sensorFromHome. The gate releases on " +
                    "the at-home sensor; with none it would drive home forever or release on nothing.");

            var coils = io.Covers
                .SelectMany(c => new[]
                {
                    c.CoilToWork == null ? default : new Coil(c.Component, c.CoilToWork, false),
                    c.CoilToHome == null ? default : new Coil(c.Component, c.CoilToHome, true),
                })
                .Where(x => x.Signal != null)
                .OrderBy(x => x.Signal.Bit)
                .ToList();

            var clash = coils.GroupBy(x => x.Signal.Bit).FirstOrDefault(g => g.Count() > 1);
            if (clash != null)
                throw new InvalidOperationException(
                    $"bx1Io declares {clash.Count()} coils on output word bit {clash.Key} " +
                    $"({string.Join(", ", clash.Select(x => x.Signal.Signal))}). One bit drives one solenoid.");

            var heldOff = coils
                .Where(x => !string.Equals(x.Component, safe.Component, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new Bx1SafeStart(safe.Component, safe.CoilToWork, safe.CoilToHome,
                safe.SensorFromHome, coils, heldOff);
        }
    }
}
