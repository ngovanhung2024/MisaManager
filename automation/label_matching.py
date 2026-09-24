"""Exact caption matching for native UIA text elements."""
import unicodedata

from .domain import normalize


def label_key(value):
    text = unicodedata.normalize('NFC', str(value)).casefold()
    return ''.join(char for char in text if char.isalnum()) or ' '.join(text.split())


def missing_tone_match(actual, caption):
    return label_key(actual) == label_key(caption)


def label_score(left, right):
    return 1.0 if label_key(left) == label_key(right) else 0.0


def label_hint_matches(labels, word):
    wanted = {label_key(label) for label in labels}
    return label_key(getattr(word, 'text', '')) in wanted


def label_candidates(labels, words, *, prefix_captions=()):
    wanted = {label_key(label) for label in labels}
    return [word for word in words if label_key(getattr(word, 'text', '')) in wanted]


def tab_candidates(label, words, tab_labels, *, aliases=()):
    return label_candidates((label, *aliases), words)


def label_is_confirmed(word):
    return bool(getattr(word, 'text', '').strip())


def local_label_support(word, profile='', *, minimum=0.0):
    return (profile or 'native', word.text, 1.0) if label_is_confirmed(word) else None


def exact_layout_label_support(word, *, minimum=0.0):
    return (('native', word.text, 1.0),) if label_is_confirmed(word) else ()


def stable_label_support(text, support, *, minimum=0.0):
    return bool(support) and all(label_key(value) == label_key(text) for _, value, _ in support)


def icon_prefix_caption(labels, word):
    return None


def icon_suffix_caption(labels, word):
    return None


def resolve_label(label, words, *, reread=None, variants=(), aliases=(), diagnostic=None,
                  allow_missing_tones=False, allow_icon_prefix=False):
    diagnostic = diagnostic if diagnostic is not None else {}
    wanted = {label_key(value) for value in (label, *variants)}
    candidates = [word for word in words
                  if label_key(getattr(word, 'text', '')) in wanted
                  or label_key(getattr(word, 'text', '')) in {label_key(value) for value in aliases}]
    diagnostic.update(label=label, accepted=False,
                      candidates=[word_evidence(word) for word in candidates])
    if len(candidates) != 1:
        diagnostic['reason'] = 'ambiguous_regions' if candidates else 'no_text_region'
        return None
    diagnostic.update(accepted=True, acceptance_rule='exact_native_caption',
                      label_evidence=word_evidence(candidates[0]))
    return candidates[0]


def word_evidence(word):
    return dict(text=getattr(word, 'text', ''), box=list(getattr(word, 'box', ())),
                source='UIA3')


LABEL_PROFILES = ('native',)
LAYOUT_LABEL_PROFILES = ('native',)
EXACT_LAYOUT_LABEL_MINIMUM = 1.0
