using Microsoft.AspNetCore.Http.HttpResults;
using PoChopAudio.Services;

namespace PoChopAudio.API.Features;

/// <summary>
/// Turns a service <see cref="OutcomeFailure"/> into the HTTP status it has always produced. This
/// is the only place in the API that knows the mapping, which is what keeps the endpoints thin and
/// keeps HTTP out of <c>PoChopAudio.Services</c> entirely.
/// </summary>
internal static class OutcomeMapping
{
    /// <summary>Maps the non-404, non-validation failures to a problem response.</summary>
    internal static ProblemHttpResult ToProblem<T>(this Outcome<T> outcome) =>
        TypedResults.Problem(outcome.Message, statusCode: outcome.Failure switch
        {
            OutcomeFailure.Empty => StatusCodes.Status400BadRequest,
            OutcomeFailure.TooLarge => StatusCodes.Status413PayloadTooLarge,
            OutcomeFailure.UnsupportedMedia => StatusCodes.Status415UnsupportedMediaType,
            OutcomeFailure.Undecodable => StatusCodes.Status422UnprocessableEntity,
            OutcomeFailure.EngineUnavailable => StatusCodes.Status503ServiceUnavailable,

            // NotFound and Invalid have richer HTTP shapes (NotFound<string>, ValidationProblem)
            // that the endpoints return directly, so they never reach here.
            _ => StatusCodes.Status500InternalServerError,
        });

    /// <summary><see cref="TypedResults.ValidationProblem"/> wants a mutable dictionary.</summary>
    internal static Dictionary<string, string[]> ToErrorDictionary<T>(this Outcome<T> outcome) =>
        new(outcome.Errors);
}
