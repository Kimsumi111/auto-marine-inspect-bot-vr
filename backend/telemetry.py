"""Per-call metadata sink; no prompts, credentials or raw exception text."""
from contextvars import ContextVar

sink = ContextVar("llm_metadata_sink", default=None)


def response_metadata(response):
    callback = sink.get()
    if callback:
        usage = getattr(response, "usage", None)
        callback(dict(request_id=getattr(response, "_request_id", None),
                      response_id=getattr(response, "id", None),
                      model=getattr(response, "model", None),
                      input_tokens=getattr(usage, "input_tokens", None),
                      output_tokens=getattr(usage, "output_tokens", None),
                      total_tokens=getattr(usage, "total_tokens", None)))
