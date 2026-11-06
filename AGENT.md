# 배포 규칙

- 배포할 때 `scripts/Publish-Release.ps1`을 사용합니다.
- `publish` 폴더도 같은 배포본으로 갱신합니다. 실행 파일만 따로 복사하지 않습니다.
- 기존 `publish`는 `artifacts`에 백업합니다. 사용자 DB와 설정은 교체하지 않습니다.
- GitHub 릴리즈는 사용자가 요청한 경우에만 게시합니다.

# 참고 자료

- [attention-span](https://github.com/alexgreensh/attention-span)
