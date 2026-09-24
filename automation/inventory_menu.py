"""Read-only caption detection for an open product menu."""


def menu_caption_words(words, header_bottom, matches):
    """Pass complete native text elements to the matcher inside the menu band."""
    nearby = [word for word in words
              if header_bottom + 2 <= word.box[1] <= header_bottom + 90]
    return [word for word in nearby if matches(word)]
