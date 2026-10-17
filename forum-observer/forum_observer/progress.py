"""Counters backed exclusively by verified current-issue article operations."""
import hashlib
import copy
from datetime import datetime, timezone


class ArticleProgress:
    def __init__(self):
        self.groups = {name: {'queued': 0, 'attempted': 0, 'opened': 0,
                              'extracted': 0, 'completed': 0}
                       for name in ('featured', 'ordinary')}
        self.last_verified_post = None
        self.last_attempted_post = None
        self.first_ordinary_attempt = None
        self.priority_violations = 0

    def group(self, item):
        return self.groups['featured' if item.get('is_featured') is True else 'ordinary']

    def queue(self, items):
        for item in items:
            self.group(item)['queued'] += 1

    def mark(self, item, stage):
        self.group(item)[stage] += 1
        if stage == 'attempted':
            self.last_attempted_post = {
                'author': item['author'], 'is_featured': item.get('is_featured') is True,
                'url': item.get('href') or item.get('detail_url'),
                'attempt_index': sum(g['attempted'] for g in self.groups.values())}
            if item.get('is_featured') is not True:
                featured = self.groups['featured']
                if self.first_ordinary_attempt is None:
                    self.first_ordinary_attempt = {
                        'at': datetime.now(timezone.utc).isoformat(),
                        'featured_queued': featured['queued'],
                        'featured_attempted': featured['attempted'],
                        'featured_completed': featured['completed']}
                if featured['completed'] < featured['queued']:
                    self.priority_violations += 1

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
                'last_verified_post': self.last_verified_post,
                'last_attempted_post': copy.deepcopy(self.last_attempted_post),
                'priority_audit': {
                    'policy': 'featured_article_before_ordinary',
                    'first_ordinary_attempt': copy.deepcopy(self.first_ordinary_attempt),
                    'violation_count': self.priority_violations,
                    'state': ('violated' if self.priority_violations else
                              'verified' if self.first_ordinary_attempt else 'ordinary_not_started')}}
