"""Counters backed exclusively by verified current-issue article operations."""
import hashlib
from datetime import datetime, timezone


class ArticleProgress:
    def __init__(self):
        self.groups = {name: {'queued': 0, 'attempted': 0, 'opened': 0,
                              'extracted': 0, 'completed': 0}
                       for name in ('featured', 'ordinary')}
        self.last_verified_post = None

    def group(self, item):
        return self.groups['featured' if item.get('is_featured') is True else 'ordinary']

    def queue(self, items):
        for item in items:
            self.group(item)['queued'] += 1

    def mark(self, item, stage):
        self.group(item)[stage] += 1

    def extracted(self, item, url, text):
        if not text:
            return
        self.mark(item, 'extracted')
        self.last_verified_post = {
            'author': item['author'], 'url': url,
            'verified_at': datetime.now(timezone.utc).isoformat(),
            'text_length': len(text),
            'text_sha256': hashlib.sha256(text.encode()).hexdigest()}

    def snapshot(self):
        groups = {name: dict(counts) for name, counts in self.groups.items()}
        return {'article_details': groups,
                'successful_post_opens': sum(g['opened'] for g in groups.values()),
                'article_texts_extracted': sum(g['extracted'] for g in groups.values()),
                'article_details_completed': sum(g['completed'] for g in groups.values()),
                'last_verified_post': self.last_verified_post}
