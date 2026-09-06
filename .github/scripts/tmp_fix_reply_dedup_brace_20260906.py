from pathlib import Path

path = Path('src/Bot/ChromeNs/ReplyDeduplicationService.cs')
text = path.read_text(encoding='utf-8-sig')
old = '''            try\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n            {\n                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);\n'''
new = '''            try\n            {\n                cancellationToken.ThrowIfCancellationRequested();\n                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);\n'''
if text.count(old) != 1:
    raise SystemExit(f'expected exactly one broken brace pattern, found {text.count(old)}')
path.write_text(text.replace(old, new, 1), encoding='utf-8-sig')
print('fixed ReplyDeduplicationService brace')
