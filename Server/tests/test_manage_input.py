from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from services.tools.manage_input import manage_input, ALL_ACTIONS


# --- Helpers ---

def _make_mocks(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_input.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_input.send_with_unity_instance",
        fake_send,
    )
    return captured


# --- Action validation ---

def test_unknown_action_returns_error():
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="nonexistent_action",
        )
    )
    assert result["success"] is False
    assert "Unknown action" in result["message"]


def test_unknown_action_with_known_prefix():
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="key_wiggle",
        )
    )
    assert result["success"] is False
    assert "key_" in result["message"]


def test_action_case_insensitive(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="KEY_DOWN",
            key="W",
        )
    )
    assert result["success"] is True
    assert captured["params"]["action"] == "key_down"


# --- Keyboard actions ---

def test_key_down_forwards_correctly(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="key_down",
            key="Space",
        )
    )
    assert result["success"] is True
    assert captured["tool_name"] == "manage_input"
    assert captured["params"] == {"action": "key_down", "key": "Space"}


def test_key_up_forwards_correctly(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="key_up",
            key="W",
        )
    )
    assert result["success"] is True
    assert captured["params"] == {"action": "key_up", "key": "W"}


def test_key_press_with_duration(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="key_press",
            key="E",
            duration=0.5,
        )
    )
    assert result["success"] is True
    assert captured["params"]["action"] == "key_press"
    assert captured["params"]["key"] == "E"
    assert captured["params"]["duration"] == 0.5


# --- Mouse actions ---

def test_mouse_move_forwards_position(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="mouse_move",
            position=[400.0, 300.0],
        )
    )
    assert result["success"] is True
    assert captured["params"]["position"] == [400.0, 300.0]


def test_mouse_click_with_position(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="mouse_click",
            button=0,
            position=[500.0, 250.0],
            duration=0.1,
        )
    )
    assert result["success"] is True
    assert captured["params"]["button"] == 0
    assert captured["params"]["position"] == [500.0, 250.0]


def test_mouse_button_down(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="mouse_button_down",
            button=1,
        )
    )
    assert result["success"] is True
    assert captured["params"]["button"] == 1


def test_mouse_scroll(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="mouse_scroll",
            scroll_delta=-3.0,
        )
    )
    assert result["success"] is True
    assert captured["params"]["scroll_delta"] == -3.0


# --- Touch ---

def test_touch_forwards_params(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="touch",
            phase="began",
            finger_id=0,
            position=[200.0, 400.0],
        )
    )
    assert result["success"] is True
    assert captured["params"]["phase"] == "began"
    assert captured["params"]["finger_id"] == 0
    assert captured["params"]["position"] == [200.0, 400.0]


# --- Gamepad ---

def test_gamepad_button(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="gamepad_button",
            button_name="south",
            pressed=True,
        )
    )
    assert result["success"] is True
    assert captured["params"]["button_name"] == "south"
    assert captured["params"]["pressed"] is True


def test_gamepad_axis_stick(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="gamepad_axis",
            axis_name="leftStick",
            axis_value=[0.5, -0.3],
        )
    )
    assert result["success"] is True
    assert captured["params"]["axis_name"] == "leftStick"
    assert captured["params"]["axis_value"] == [0.5, -0.3]


def test_gamepad_axis_trigger(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="gamepad_axis",
            axis_name="leftTrigger",
            axis_value=0.8,
        )
    )
    assert result["success"] is True
    assert captured["params"]["axis_value"] == 0.8


# --- UI ---

def test_click_ui(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="click_ui",
            element_name="StartButton",
        )
    )
    assert result["success"] is True
    assert captured["params"]["element_name"] == "StartButton"


# --- Query ---

def test_get_status(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="get_status",
        )
    )
    assert result["success"] is True
    assert captured["params"] == {"action": "get_status"}


def test_get_input_state(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="get_input_state",
        )
    )
    assert result["success"] is True


# --- Sequences ---

def test_send_sequence(monkeypatch):
    captured = _make_mocks(monkeypatch)
    steps = [
        {"action": "key_down", "params": {"key": "W"}, "delay": 0},
        {"action": "key_up", "params": {"key": "W"}, "delay": 1.0},
    ]
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="send_sequence",
            sequence=steps,
        )
    )
    assert result["success"] is True
    assert captured["params"]["sequence"] == steps


def test_get_sequence_status(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="get_sequence_status",
            job_id="abc123",
        )
    )
    assert result["success"] is True
    assert captured["params"]["job_id"] == "abc123"


# --- None params omitted ---

def test_none_params_not_sent(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="key_down",
            key="A",
            # All other params are None by default
        )
    )
    assert result["success"] is True
    # Only action and key should be present
    assert set(captured["params"].keys()) == {"action", "key"}


# --- ALL_ACTIONS consistency ---

def test_all_actions_list_is_complete():
    expected = {
        "key_down", "key_up", "key_press",
        "mouse_move", "mouse_button_down", "mouse_button_up", "mouse_click", "mouse_scroll",
        "touch",
        "gamepad_button", "gamepad_axis",
        "click_ui",
        "get_input_state", "get_status",
        "send_sequence", "get_sequence_status",
        "move_to", "query_surroundings", "wait_turns",
    }
    assert set(ALL_ACTIONS) == expected


# --- Navigation actions ---

def test_move_to_by_name(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="move_to",
            target="chest",
        )
    )
    assert result["success"] is True
    assert captured["params"]["target"] == "chest"
    assert captured["params"]["action"] == "move_to"


def test_move_to_by_coords(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="move_to",
            x=43,
            y=11,
        )
    )
    assert result["success"] is True
    assert captured["params"]["x"] == 43
    assert captured["params"]["y"] == 11


def test_move_to_with_max_steps(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="move_to",
            target="elder",
            max_steps=10,
        )
    )
    assert result["success"] is True
    assert captured["params"]["max_steps"] == 10


def test_query_surroundings(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="query_surroundings",
            radius=5,
        )
    )
    assert result["success"] is True
    assert captured["params"]["radius"] == 5


def test_query_surroundings_default(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="query_surroundings",
        )
    )
    assert result["success"] is True
    assert "radius" not in captured["params"]


def test_wait_turns(monkeypatch):
    captured = _make_mocks(monkeypatch)
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="wait_turns",
            count=5,
        )
    )
    assert result["success"] is True
    assert captured["params"]["count"] == 5
