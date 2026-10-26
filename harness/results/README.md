# Results

`<timestamp>_<set_id>/summary.md`, `config.json`, `metrics.json`을 보존한다. 대용량 artifact만 `artifacts/`에 두며 Git에서 제외한다.

예외 없이 끝난 실행과 알고리즘 판정은 별개다. `summary.md`에 `ACCEPT`, `REJECT`, `NEEDS_MORE_TEST`를 함께 기록하고, 잘못된 결과도 삭제하지 않는다.
