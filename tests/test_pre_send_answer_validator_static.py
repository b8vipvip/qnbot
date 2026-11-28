from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_validator_has_pass_regenerate_manual_actions():
    code = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")
    assert "AnswerValidationAction" in code
    assert "Pass" in code
    assert "Regenerate" in code
    assert "Manual" in code
    assert "BuildRegenerationInstruction" in code
    assert "secondAttempt" in code


def test_validator_checks_unsupported_claims_and_question_coverage():
    code = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")
    assert "ConcreteNumberRegex" in code
    assert "AbsolutePromisePhrases" in code
    assert "ConcreteStatusClaims" in code
    assert "HasKnowledgeContradiction" in code
    assert "询问价格" in code
    assert "询问时间/时效" in code
    assert "确认是否支持" in code
    assert "询问操作方法" in code
    assert "故障排查" in code
    assert "系统提示词" in code
    assert "语言模型" in code


def test_dedup_pipeline_validates_before_real_send_and_repairs_once():
    code = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    first_validate = code.index("PreSendAnswerValidator.Validate(")
    repair = code.index("RegenerateInvalidAnswer(", first_validate)
    second_validate = code.index("PreSendAnswerValidator.Validate(", repair)
    marker = code.index("BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer)", second_validate)
    assert first_validate < repair < second_validate < marker
    assert 'validationSource = "AI校验重答"' in code
    assert "BuildBlockedResult" in code
    assert "修正后的答案仍未通过发送前校验" in code
    assert "发送前校验重答失败" in code


def test_trusted_exact_local_knowledge_does_not_trigger_extra_ai_call():
    code = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    guard = code.index("if (preserveTrustedAnswer)")
    validate = code.index("PreSendAnswerValidator.Validate(", guard)
    assert guard < validate
    assert 'Source = "权威知识V2"' in code[guard:validate]
    assert "return new ReplyDeduplicationResult" in code[guard:validate]
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "IsV2OfflineFallbackCurrent(" in pipeline
    assert "answer, lease.CancellationToken, false," in pipeline


def test_build_targets_include_validator():
    targets = read("src/Directory.Build.targets")
    assert "PreSendAnswerValidator.cs" in targets
