using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Validation
{
    public class ComponentValidator
    {
        public ValidationResult Validate(VueOneComponent component)
        {
            var result = new ValidationResult();

            ValidateBasicProperties(component, result);
            ValidateStates(component, result);

            return result;
        }

        private void ValidateBasicProperties(VueOneComponent component, ValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(component.Name))
                result.AddError("Component Name is empty");
            else
                result.AddInfo($"✓ Component Name: {component.Name}");

            if (string.IsNullOrWhiteSpace(component.Type))
                result.AddError("Component Type is empty");
            else if (component.Type != "Actuator" && component.Type != "Sensor")
                result.AddWarning($"Component Type '{component.Type}' may not have a matching template");
            else
                result.AddInfo($"✓ Component Type: {component.Type}");

            ValidateStateCount(component, result);
        }

        private void ValidateStateCount(VueOneComponent component, ValidationResult result)
        {
            var count = component.States.Count;

            if (count == 0)
            {
                result.AddError("Component has no states");
                return;
            }

            var isValidPattern = (count == 5 && component.Type == "Actuator") ||
                                (count == 7 && component.Type == "Actuator") ||
                                (count == 2 && component.Type == "Sensor");

            if (isValidPattern)
            {
                var pattern = count switch
                {
                    5 => "Five-State Actuator",
                    7 => "Seven-State Actuator",
                    2 => "Two-State Sensor",
                    _ => "Unknown"
                };
                result.AddInfo($"✓ State Count: {count} (matches {pattern} pattern)");
            }
            else
            {
                result.AddWarning($"State Count: {count} (may not match available templates)");
            }
        }

        private void ValidateStates(VueOneComponent component, ValidationResult result)
        {
            var hasInitialState = false;

            for (int i = 0; i < component.States.Count; i++)
            {
                var state = component.States[i];

                if (state.StateNumber != i)
                    result.AddError($"State '{state.Name}' has State_Number={state.StateNumber}, expected {i}");
                else
                    result.AddInfo($"✓ State {i}: Name='{state.Name}', State_Number={state.StateNumber}");

                if (state.InitialState)
                {
                    if (hasInitialState)
                        result.AddError($"Multiple states marked as Initial_State (State '{state.Name}')");
                    else if (state.StateNumber != 0)
                        result.AddWarning($"Initial_State is '{state.Name}' (State {state.StateNumber}), expected State 0");
                    else
                        result.AddInfo($"✓ Initial_State: '{state.Name}' (State 0)");

                    hasInitialState = true;
                }
                if (!hasInitialState)
                {
                    if (component.Type == "Sensor")
                        result.AddWarning("Sensors are reactive - no initial state required");
                    else
                        result.AddError("No state marked as Initial_State");
                }

                if (state.Time > 0)
                    result.AddInfo($"⚠ State '{state.Name}' Time={state.Time}ms (DISCARDED - VueOne specific)");

                if (state.Position != 0)
                    result.AddInfo($"⚠ State '{state.Name}' Position={state.Position} (DISCARDED - PLC setpoint)");
            }

            if (!hasInitialState)
            {
                if (component.Type == "Sensor")
                    result.AddWarning("Sensors are reactive - no initial state required");
                else
                    result.AddError("No state marked as Initial_State");
            }
        }
    }

    public class ValidationResult
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> InfoMessages { get; } = new();

        public bool IsValid => Errors.Count == 0;

        public void AddError(string message) => Errors.Add(message);
        public void AddWarning(string message) => Warnings.Add(message);
        public void AddInfo(string message) => InfoMessages.Add(message);

        public void PrintToConsole()
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("VALIDATION RESULTS");
            Console.WriteLine(new string('=', 60));

            PrintMessages(InfoMessages, ConsoleColor.White);
            PrintMessages(Warnings, ConsoleColor.Yellow, "⚠ WARNING: ");
            PrintMessages(Errors, ConsoleColor.Red, "✗ ERROR: ");

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(new string('=', 60));

            Console.ForegroundColor = IsValid ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(IsValid
                ? "VALIDATION PASSED - Ready for translation"
                : "VALIDATION FAILED - Translation rejected");

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(new string('=', 60) + "\n");
        }

        private void PrintMessages(List<string> messages, ConsoleColor color, string prefix = "")
        {
            if (messages.Count == 0) return;

            Console.WriteLine();
            Console.ForegroundColor = color;
            messages.ForEach(msg => Console.WriteLine(prefix + msg));
        }
    }
}