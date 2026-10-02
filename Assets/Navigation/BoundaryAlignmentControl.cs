using System;

namespace ShipRobot.Navigation
{
    public static class BoundaryAlignmentControl
    {
        public static float ExitHeadingTurn(float headingErrorDegrees, float maximumTurn)
        {
            if (Math.Abs(headingErrorDegrees) <= 4f) return 0f;
            return Math.Sign(headingErrorDegrees) * Math.Min(maximumTurn,
                Math.Max(0.05f, Math.Abs(headingErrorDegrees) / 45f * maximumTurn));
        }

        public static float ConstrainToExitHeading(float visualTurn, float headingErrorDegrees,
            float headingTolerance, float maximumTurn)
        {
            if (Math.Abs(headingErrorDegrees) >= headingTolerance)
                return ExitHeadingTurn(headingErrorDegrees, maximumTurn);
            // At the edge of the permitted heading range, reject commands that turn farther away.
            if (Math.Abs(headingErrorDegrees) >= headingTolerance * 0.8f &&
                visualTurn * headingErrorDegrees < 0f) return 0f;
            return Math.Max(-maximumTurn, Math.Min(maximumTurn, visualTurn));
        }

        public static (float move, float turn) Calculate(float angleRadians, float lateralError,
            float angleGain, float lateralGain, float maximumTurn,
            float forwardMove)
        {
            float turn = (float)Math.Sin(angleRadians) * angleGain + lateralError * lateralGain;
            turn = Math.Max(-maximumTurn, Math.Min(maximumTurn, turn));
            float move = Math.Max(0f, Math.Min(1f, forwardMove));
            return (move, turn);
        }
    }
}
