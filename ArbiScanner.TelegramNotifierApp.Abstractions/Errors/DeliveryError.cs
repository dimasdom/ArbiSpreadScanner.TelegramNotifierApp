using FluentResults;

namespace ArbiScanner.TelegramNotifierApp.Abstractions.Errors;

// Recoverable: Telegram/network hiccup, timeout, or 429 - worth retrying on the next spread.
public class TransientDeliveryError(string message) : Error(message);

// Not recoverable by retrying: bot blocked, chat not found, or a malformed message -
// callers should stop sending to this recipient rather than keep trying.
public class PermanentDeliveryError(string message) : Error(message);
